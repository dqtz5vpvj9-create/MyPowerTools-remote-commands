using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Android;

/// <summary>
/// Remote Commands adapter for the MyPowerTools Android host.
///
/// It is a normal MPT module - the same lifecycle, commands, settings, events and tool-surface contract
/// as every other module - but it executes over a fully managed SSH client instead of the desktop
/// ssh.exe/scp.exe pair, which does not exist on Android.
///
/// Product behaviour is shared, not re-implemented:
/// <list type="bullet">
///   <item>commands.yaml schema, validation and defaults: the shipped
///   <see cref="RemoteCommandsYaml"/> source is compiled in;</item>
///   <item>settings.json / history.json / commands.yaml persistence: the shipped
///   <see cref="RemoteCommandsStore"/> source is compiled in and reads the same tool data directory
///   the phone surface edits, so the catalog cannot fork;</item>
///   <item>local <c>py</c> transforms: the shipped <see cref="RemoteCommandsTextTransforms"/>
///   source;</item>
///   <item>the remote command line keeps the desktop contract
///   (<c>CONDA_EXE=&lt;path&gt; &lt;command&gt; --file1 ... --file2 ...</c>) so a command definition means the
///   same thing on both platforms;</item>
///   <item>credentials live only in the platform <c>secret.store</c>; host aliases are mapped to real
///   addresses through an Android-only catalog because there is no <c>~/.ssh/config</c> on the
///   phone;</item>
///   <item>host keys are verified with no default trust: first use and key rotation both require an
///   explicit confirmation of the exact SHA256 fingerprint;</item>
///   <item>runs happen only when the user asks for one, hold a <c>background.activity</c> lease while
///   in flight, and cancel/disable kills the command, drops the connection and releases the lease.</item>
/// </list>
/// </summary>
public sealed class RemoteCommandsAndroidModule : IMptModule, IMptModuleLifecycle
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // Bounded so a surface that is not looking at the page cannot grow memory without limit; the
    // surface reconciles with the final command result, which always carries the full output.
    private readonly Channel<MptModuleEvent> _events = Channel.CreateBounded<MptModuleEvent>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConcurrentDictionary<string, StreamSink> _streams = new(StringComparer.Ordinal);
    private readonly OutputBatcher _outputBatcher;

    private ModuleContext? _context;
    private ISecretStore? _secrets;
    private IBackgroundActivityService? _background;
    private RemoteCommandsAndroidSecrets? _secretAccess;
    private RemoteCommandsAndroidHostCatalog? _hostCatalog;
    private RemoteCommandsAndroidHostKeyStore? _hostKeys;
    private RemoteCommandsAndroidRunner? _runner;
    private RemoteCommandsStore? _store;
    private string _dataRoot = "";
    private string _preferencesPath = "";
    private RemoteCommandsAndroidPreferences _preferences = new();
    private IReadOnlyList<RemoteCommandDefinition> _commands = [];
    private string _commandsError = "";
    private long _settingsRevision = 1;
    private long _eventSeq;
    private string _lastRunState = "idle";
    private string _lastRunSummary = "尚未执行过远程命令";
    private bool _disposed;

    public RemoteCommandsAndroidModule()
    {
        _outputBatcher = new OutputBatcher(FlushOutput);
    }

    public string Id => RemoteCommandsAndroidOptions.ModuleId;

    public string PackageId => RemoteCommandsAndroidOptions.PackageId;

    public Version Version => RemoteCommandsAndroidOptions.ModuleVersion;

    /// <summary>Test seam: replaces the SSH.NET transport so tests never open a socket.</summary>
    internal ISshTransport? TransportOverride { get; set; }

    private ModuleContext Context =>
        _context ?? throw new InvalidOperationException("远程命令模块尚未初始化。");

    private RemoteCommandsStore Store =>
        _store ?? throw new InvalidOperationException("远程命令模块尚未初始化。");

    private RemoteCommandsAndroidHostCatalog HostCatalog =>
        _hostCatalog ?? throw new InvalidOperationException("远程命令模块尚未初始化。");

    private RemoteCommandsAndroidHostKeyStore HostKeys =>
        _hostKeys ?? throw new InvalidOperationException("远程命令模块尚未初始化。");

    private RemoteCommandsAndroidSecrets SecretAccess =>
        _secretAccess ?? throw new InvalidOperationException("远程命令模块尚未初始化。");

    private RemoteCommandsAndroidRunner Runner =>
        _runner ?? throw new InvalidOperationException("远程命令模块尚未初始化。");

    // ---------------------------------------------------------------- lifecycle

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _disposed = false;

        _dataRoot = RemoteCommandsAndroidPaths.ResolveDataRoot(context.DataDirectory);
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);

        _secrets = context.GetCapability<ISecretStore>("secret.store");
        context.TryGetCapability<IBackgroundActivityService>("background.activity", out _background);

        _preferencesPath = RemoteCommandsAndroidPaths.PreferencesPath(_dataRoot);
        _preferences = RemoteCommandsAndroidPreferences.Load(_preferencesPath);
        _store = new RemoteCommandsStore(_dataRoot);
        _store.EnsureInitialized();
        _secretAccess = new RemoteCommandsAndroidSecrets(_secrets);
        _hostCatalog = new RemoteCommandsAndroidHostCatalog(RemoteCommandsAndroidPaths.HostsPath(_dataRoot));
        _hostKeys = new RemoteCommandsAndroidHostKeyStore(RemoteCommandsAndroidPaths.HostKeysPath(_dataRoot));
        _runner = new RemoteCommandsAndroidRunner(
            TransportOverride ?? (ISshTransport?)SshNetTransport.TryCreate() ?? new UnavailableSshTransport(),
            _hostCatalog,
            _hostKeys,
            _secretAccess,
            _background,
            () => _preferences,
            Publish,
            message => Trace.WriteLine($"Remote Commands (Android): {message}"));

        LoadCommands();

        return ValueTask.FromResult(new InitializeResult(
            true,
            context.ProtocolVersion,
            ["lifecycle", "status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    /// <summary>Enabling the module never connects to a server; a run is always a user action.</summary>
    public ValueTask EnableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish("module.running", new JsonObject
        {
            ["title"] = RemoteCommandsAndroidOptions.DisplayName,
            ["message"] = "远程命令模块已就绪。连接只会在你点击运行时建立。",
            ["state"] = "ready"
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>Cancels the in-flight run and releases its connection and foreground-service lease.</summary>
    public async ValueTask StopAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _runner?.Cancel(null);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        _operations.Release();
    }

    public async ValueTask DisableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        await StopAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            _runner?.Cancel(null);
        }
        catch (Exception)
        {
            // Release is best effort during teardown.
        }

        _outputBatcher.Dispose();
        _events.Writer.TryComplete();
        foreach (var sink in _streams.Values)
        {
            sink.Channel.Writer.TryComplete();
        }

        _streams.Clear();
        _operations.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // ---------------------------------------------------------------- status

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var settings = Store.LoadSettings();
        var hosts = HostCatalog.Hosts;
        var credentialCount = 0;
        foreach (var host in hosts)
        {
            if (await SecretAccess.HasCredentialAsync(host, cancellationToken).ConfigureAwait(false))
            {
                credentialCount++;
            }
        }

        var transportAvailable = _runner is not null && (TransportOverride?.IsAvailable ?? SshNetTransport.IsSupported);
        var checks = new[]
        {
            new HealthCheckSnapshot(
                "remote-commands.catalog",
                "commands.yaml",
                _commands.Count > 0,
                _commandsError.Length > 0
                    ? _commandsError
                    : $"{_commands.Count} 条命令来自 {RemoteCommandsAndroidOptions.CommandsFileName}。"),
            new HealthCheckSnapshot(
                "remote-commands.hosts",
                "真实主机映射",
                hosts.Count > 0 && credentialCount > 0,
                hosts.Count == 0
                    ? "Android 没有 ~/.ssh/config：请在“主机”中为 commands.yaml 里的别名填写真实主机、端口、用户名与凭据。"
                    : $"{hosts.Count} 台主机，其中 {credentialCount} 台已保存凭据。" +
                      (credentialCount < hosts.Count ? " 缺少凭据的主机无法运行。" : "")),
            new HealthCheckSnapshot(
                "remote-commands.host-keys",
                "主机密钥确认",
                true,
                HostKeys.Keys.Count == 0
                    ? "还没有确认过主机密钥；首次连接会显示指纹并要求你确认。"
                    : $"已确认 {HostKeys.Keys.Count} 个主机密钥，密钥变化时会拒绝连接并提示。"),
            new HealthCheckSnapshot(
                "remote-commands.transport",
                "纯托管 SSH",
                transportAvailable,
                transportAvailable
                    ? "使用托管 SSH 客户端（不调用 ssh.exe / scp.exe）。"
                    : "当前构建缺少可用的 SSH 传输。"),
            new HealthCheckSnapshot(
                "remote-commands.background",
                "后台任务能力",
                _background is not null,
                _background is not null
                    ? "执行期间会持有前台服务任务，可在通知里停止。"
                    : "当前主机未提供 background.activity：退到后台后系统可能结束执行。"),
            new HealthCheckSnapshot(
                "remote-commands.run",
                "执行状态",
                _lastRunState is "idle" or "succeeded",
                _lastRunSummary)
        };

        var healthy = _commands.Count > 0 && transportAvailable;
        return new ModuleStatusSnapshot(
            Id,
            healthy ? "running" : "degraded",
            healthy
                ? $"{_commands.Count} 条命令，{hosts.Count} 台主机。{_lastRunSummary}"
                : FirstFailure(checks),
            DateTimeOffset.UtcNow,
            checks,
            (ulong)Interlocked.Read(ref _eventSeq));
    }

    private static string FirstFailure(IReadOnlyList<HealthCheckSnapshot> checks) =>
        checks.FirstOrDefault(check => !check.Ok)?.Message ?? "远程命令需要检查。";

    // ---------------------------------------------------------------- commands

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command(RemoteCommandsAndroidOptions.CommandStatus, "查看远程命令状态", "命令目录、主机、主机密钥与执行状态"),
            Command(RemoteCommandsAndroidOptions.CommandCatalog, "列出命令目录", "读取共享的 commands.yaml"),
            Command(
                RemoteCommandsAndroidOptions.CommandCatalogSave,
                "保存命令目录",
                "校验并写入共享的 commands.yaml（只接受文档内容，不接受路径）"),
            Command(
                RemoteCommandsAndroidOptions.CommandSettingsUpdate,
                "保存设置",
                "校验并保存默认主机、别名、历史保留、CONDA_EXE 与执行超时"),
            Command(
                RemoteCommandsAndroidOptions.CommandRun,
                "运行远程命令",
                "上传两个输入文件、执行用户配置的命令并输出结果",
                timeoutMs: 3600000,
                supportsProgress: true,
                supportsCancellation: true),
            Command(RemoteCommandsAndroidOptions.CommandCancel, "取消执行", "结束当前远程命令并断开连接"),
            Command(RemoteCommandsAndroidOptions.CommandHostsList, "列出主机", "显示别名到真实主机、用户与凭据状态的映射"),
            Command(RemoteCommandsAndroidOptions.CommandHostAdd, "保存主机", "保存真实主机、端口、用户名与凭据（凭据进入系统凭据库）"),
            Command(RemoteCommandsAndroidOptions.CommandHostRemove, "删除主机", "删除主机映射及其系统凭据库中的凭据"),
            Command(RemoteCommandsAndroidOptions.CommandHostKeyStatus, "查看主机密钥", "显示已确认的指纹与上一次待确认的指纹"),
            Command(RemoteCommandsAndroidOptions.CommandHostKeyAccept, "确认主机密钥", "用指纹明确确认首次连接或已轮换的主机密钥"),
            Command(RemoteCommandsAndroidOptions.CommandHostKeyRevoke, "撤销主机密钥", "删除已确认的指纹，下次连接重新确认"),
            Command(RemoteCommandsAndroidOptions.CommandHistorySummary, "历史摘要", "统计共享 history.json"),
            Command(RemoteCommandsAndroidOptions.CommandHistoryClear, "清空历史", "删除本机保存的执行历史"),
            Command(RemoteCommandsAndroidOptions.CommandTransform, "本地文本转换", "执行 py 类型命令，不联网")
        ];
        return ValueTask.FromResult(commands);
    }

    private static MptCommandDescriptor Command(
        string id,
        string title,
        string subtitle,
        int timeoutMs = 15000,
        bool supportsProgress = false,
        bool supportsCancellation = false) =>
        new(
            id,
            RemoteCommandsAndroidOptions.ModuleId,
            title,
            subtitle,
            "action",
            Category: RemoteCommandsAndroidOptions.DisplayName,
            TimeoutMs: timeoutMs,
            Execution: new JsonObject { ["type"] = "module.execute" },
            Constraints: [MptOperationConstraints.RunsExternalProcesses],
            SupportsProgress: supportsProgress,
            SupportsCancellation: supportsCancellation);

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.CommandId switch
            {
                RemoteCommandsAndroidOptions.CommandStatus =>
                    Succeeded(request, (await BuildStateJsonAsync(cancellationToken).ConfigureAwait(false))
                        .ToJsonString(IndentedJson)),
                RemoteCommandsAndroidOptions.CommandCatalog => Catalog(request),
                RemoteCommandsAndroidOptions.CommandCatalogSave => await SaveCatalogAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RemoteCommandsAndroidOptions.CommandSettingsUpdate =>
                    await UpdateSettingsCommandAsync(request, cancellationToken).ConfigureAwait(false),
                RemoteCommandsAndroidOptions.CommandRun => await RunAsync(request, cancellationToken).ConfigureAwait(false),
                RemoteCommandsAndroidOptions.CommandCancel => Cancel(request),
                RemoteCommandsAndroidOptions.CommandHostsList =>
                    Succeeded(request, (await BuildHostsJsonAsync(cancellationToken).ConfigureAwait(false))
                        .ToJsonString(IndentedJson)),
                RemoteCommandsAndroidOptions.CommandHostAdd => await AddHostAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RemoteCommandsAndroidOptions.CommandHostRemove => await RemoveHostAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RemoteCommandsAndroidOptions.CommandHostKeyStatus =>
                    Succeeded(request, BuildHostKeysJson().ToJsonString(IndentedJson)),
                RemoteCommandsAndroidOptions.CommandHostKeyAccept => AcceptHostKey(request),
                RemoteCommandsAndroidOptions.CommandHostKeyRevoke => RevokeHostKey(request),
                RemoteCommandsAndroidOptions.CommandHistorySummary => HistorySummary(request),
                RemoteCommandsAndroidOptions.CommandHistoryClear => await ClearHistoryAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                RemoteCommandsAndroidOptions.CommandTransform => Transform(request),
                _ => Failed(
                    request,
                    RemoteCommandsAndroidErrorCodes.NotFound,
                    $"远程命令模块未实现命令 '{request.CommandId}'。")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(request, ClassifyError(exception), exception.Message);
        }
    }

    public async IAsyncEnumerable<CommandExecutionEvent> ExecuteCommandStreamAsync(
        CommandRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        // Only a run streams: it registers an invocation sink so output lines reach the command
        // palette as progress. Every other command is a single terminal event.
        if (!string.Equals(request.CommandId, RemoteCommandsAndroidOptions.CommandRun, StringComparison.Ordinal))
        {
            var only = await ExecuteCommandAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new CommandExecutionEvent(
                only.InvocationId,
                only.CommandId,
                only.State,
                only.Success ? only.Output : only.Error?.Message ?? "命令执行失败。",
                1,
                true,
                only);
            yield break;
        }

        var sink = new StreamSink();
        _streams[request.InvocationId] = sink;
        try
        {
            var runTask = ExecuteCommandAsync(request, cancellationToken).AsTask();
            // Completion must end the reader loop even when the run publishes nothing at all
            // (validation failure, unknown command), otherwise this method would wait forever.
            _ = runTask.ContinueWith(
                _ => sink.Channel.Writer.TryComplete(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await foreach (var item in sink.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            var result = await runTask.ConfigureAwait(false);
            yield return new CommandExecutionEvent(
                result.InvocationId,
                result.CommandId,
                result.State,
                result.Success ? result.Output : result.Error?.Message ?? "命令执行失败。",
                (int)Interlocked.Increment(ref sink.Sequence),
                true,
                result);
        }
        finally
        {
            _streams.TryRemove(request.InvocationId, out _);
            sink.Channel.Writer.TryComplete();
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>
    /// Streams module events. Disposal completes the channel, which ends the enumeration normally
    /// instead of throwing into a subscriber that is already tearing down.
    /// </summary>
    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.Seq > cursor.LastEventSeq)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Single publish path for module events and command-stream progress. Output lines are batched by
    /// <see cref="_outputBatcher"/> so a chatty remote command cannot flood the event pump, while the
    /// final result still carries the full (bounded) output.
    /// </summary>
    private void Publish(string type, JsonObject payload)
    {
        // A single output line is buffered, never published one event per line.
        if (string.Equals(type, "command.output", StringComparison.Ordinal))
        {
            var lineInvocation = payload["invocationId"]?.GetValue<string>() ?? "";
            var line = payload["line"]?.GetValue<string>();
            if (lineInvocation.Length > 0 && line is not null)
            {
                _outputBatcher.Add(lineInvocation, line);
                return;
            }
        }

        _outputBatcher.FlushIfPending(type);

        var sequence = (ulong)Interlocked.Increment(ref _eventSeq);
        _events.Writer.TryWrite(new MptModuleEvent(Id, sequence, type, DateTimeOffset.UtcNow, payload));

        if (payload["invocationId"]?.GetValue<string>() is not { Length: > 0 } invocationId ||
            !_streams.TryGetValue(invocationId, out var sink))
        {
            return;
        }

        if (string.Equals(type, "command.output", StringComparison.Ordinal))
        {
            foreach (var line in (payload["lines"] as JsonArray)?.Select(node => node?.GetValue<string>() ?? "") ?? [])
            {
                sink.Channel.Writer.TryWrite(Progress(sink, invocationId, payload, line));
            }

            return;
        }

        sink.Channel.Writer.TryWrite(Progress(
            sink,
            invocationId,
            payload,
            type switch
            {
                "run.stage" => $"阶段：{payload["stage"]?.GetValue<string>()}",
                "run.started" => $"开始执行 {payload["label"]?.GetValue<string>()}",
                "run.finished" => $"结束：{payload["state"]?.GetValue<string>()}",
                _ => type
            }));
    }

    private static CommandExecutionEvent Progress(
        StreamSink sink,
        string invocationId,
        JsonObject payload,
        string message) =>
        new(
            invocationId,
            payload["commandId"]?.GetValue<string>() ?? "",
            "",
            message,
            (int)Interlocked.Increment(ref sink.Sequence),
            false);

    private void FlushOutput(string invocationId, IReadOnlyList<string> lines)
    {
        Publish("command.output", new JsonObject
        {
            ["invocationId"] = invocationId,
            ["lines"] = new JsonArray(lines.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray())
        });
    }

    /// <summary>
    /// Saves settings through the same validated path the module settings interface uses
    /// (<see cref="PersistSettingsAsync"/>): five editable keys, partial updates allowed, unknown keys
    /// rejected. No alternative persistence exists - <c>settings.json</c> and
    /// <c>android-preferences.json</c> are written by that one method only.
    ///
    /// The payload always carries the resulting preferences and a status snapshot so the caller can
    /// refresh its page from the command result alone.
    /// </summary>
    private async Task<CommandExecutionResult> UpdateSettingsCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var args = request.Args ?? new JsonObject();
        if (args[RemoteCommandsAndroidOptions.ArgumentValues] is not JsonObject values)
        {
            return SettingsRejected(request, "请提供要保存的设置（values）。", []);
        }

        var appliedKeys = values.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        try
        {
            await PersistSettingsAsync(values, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            // Nothing was written: validation runs before the first write.
            return SettingsRejected(request, exception.Message, appliedKeys);
        }

        Publish("settings.updated", new JsonObject
        {
            ["title"] = "设置已保存",
            ["message"] = $"已更新 {appliedKeys.Length} 项设置。",
            ["keys"] = new JsonArray(appliedKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray())
        });

        return Succeeded(request, BuildSettingsCommandPayload(saved: true, appliedKeys, "").ToJsonString(IndentedJson));
    }

    /// <summary>
    /// A rejected settings write is a failed command whose payload still carries the unchanged
    /// preferences, so the page can show the current values next to the validation message.
    /// </summary>
    private CommandExecutionResult SettingsRejected(
        CommandRequest request,
        string error,
        IReadOnlyList<string> appliedKeys)
    {
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            BuildSettingsCommandPayload(saved: false, appliedKeys, error).ToJsonString(IndentedJson),
            new MptRuntimeError(RemoteCommandsAndroidErrorCodes.ValidationFailed, error));
    }

    private JsonObject BuildSettingsCommandPayload(bool saved, IReadOnlyList<string> appliedKeys, string error)
    {
        var payload = new JsonObject
        {
            ["saved"] = saved,
            ["appliedKeys"] = new JsonArray(appliedKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
            ["settings"] = BuildSettingsValues(),
            ["revision"] = Interlocked.Read(ref _settingsRevision),
            ["status"] = new JsonObject
            {
                ["commandCount"] = _commands.Count,
                ["commandsError"] = _commandsError,
                ["activeStage"] = Runner.ActiveStage,
                ["activeInvocationId"] = Runner.ActiveInvocationId,
                ["lastRunState"] = _lastRunState,
                ["lastRunSummary"] = _lastRunSummary,
                ["transportAvailable"] = TransportOverride?.IsAvailable ?? SshNetTransport.IsSupported,
                ["backgroundAvailable"] = _background is not null
            }
        };
        if (error.Length > 0)
        {
            payload["saveError"] = error;
        }

        return payload;
    }

    // ---------------------------------------------------------------- settings

    private const string SchemaJson = """
    {
      "type": "object",
      "properties": {
        "defaultHost": {
          "type": "string",
          "title": "默认主机",
          "description": "命令没有写死主机时使用的别名；需要在“主机”里映射到真实地址。"
        },
        "knownHosts": {
          "type": "string",
          "title": "主机别名列表",
          "description": "每行一个别名，与桌面端 settings.json 的 knownHosts 相同。"
        },
        "historyRetention": { "type": "integer", "title": "历史条数上限", "minimum": 10, "maximum": 5000, "default": 500 },
        "condaExecutable": {
          "type": "string",
          "title": "远端的 CONDA_EXE",
          "description": "与桌面执行器一致的 CONDA_EXE 前缀路径。"
        },
        "commandTimeoutMinutes": { "type": "integer", "title": "单次执行超时（分钟）", "minimum": 1, "maximum": 1440, "default": 30 },
        "hostCount": { "type": "integer", "title": "已配置主机", "readOnly": true },
        "trustedHostKeyCount": { "type": "integer", "title": "已确认主机密钥", "readOnly": true },
        "activeRun": { "type": "string", "title": "当前执行", "readOnly": true }
      }
    }
    """;

    private static readonly string[] EditableSettingKeys =
        ["defaultHost", "knownHosts", "historyRetention", "condaExecutable", "commandTimeoutMinutes"];

    private static readonly string[] ReadOnlySettingKeys =
        ["hostCount", "trustedHostKeyCount", "activeRun"];

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, SchemaJson));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return ValueTask.FromResult(new SettingsSnapshotDocument(
            Id,
            (ulong)Interlocked.Read(ref _settingsRevision),
            BuildSettingsValues(),
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The one projection of the persisted settings: five editable values plus the read-only counters.
    /// Shared by the settings interface and the <c>settings.update</c> command so both report the same
    /// document.
    /// </summary>
    private JsonObject BuildSettingsValues()
    {
        var settings = Store.LoadSettings();
        return new JsonObject
        {
            ["defaultHost"] = settings.DefaultHost,
            ["knownHosts"] = settings.KnownHosts,
            ["historyRetention"] = settings.HistoryRetention,
            ["condaExecutable"] = _preferences.CondaExecutable,
            ["commandTimeoutMinutes"] = _preferences.CommandTimeoutMinutes,
            ["hostCount"] = HostCatalog.Hosts.Count,
            ["trustedHostKeyCount"] = HostKeys.Keys.Count,
            ["activeRun"] = Runner.ActiveStage
        };
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(
        SettingsPatch patch,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateSettingKeys(patch.Patch);
            return ValueTask.FromResult(new SettingsValidationResult(true, []));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return ValueTask.FromResult(new SettingsValidationResult(
                false,
                [exception.Message],
                new MptRuntimeError(RemoteCommandsAndroidErrorCodes.ValidationFailed, exception.Message)));
        }
    }

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(
        SettingsSnapshotDocument snapshot,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        await PersistSettingsAsync(snapshot.Values ?? new JsonObject(), cancellationToken).ConfigureAwait(false);
        return await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single validated write path for settings. Every caller - the module settings interface and
    /// the <c>settings.update</c> command - goes through here, so there is no second persistence route
    /// and no second interpretation of the four files.
    ///
    /// Validation happens before the first byte is written: an unknown key, an out-of-range number or
    /// an invalid host/alias aborts the whole update and leaves both <c>settings.json</c> and
    /// <c>android-preferences.json</c> exactly as they were.
    /// </summary>
    private async Task PersistSettingsAsync(JsonObject values, CancellationToken cancellationToken)
    {
        ValidateSettingValues(values);
        var current = Store.LoadSettings();
        var updated = current with
        {
            DefaultHost = ReadString(values, "defaultHost")?.Trim() ?? current.DefaultHost,
            KnownHosts = ReadString(values, "knownHosts") ?? current.KnownHosts,
            HistoryRetention = ReadInt(values, "historyRetention") ?? current.HistoryRetention
        };
        var preferences = _preferences.With(
            ReadString(values, "condaExecutable"),
            ReadInt(values, "commandTimeoutMinutes"));

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Store.SaveSettings(updated);
            RemoteCommandsAndroidPreferences.Save(_preferencesPath, preferences);
            _preferences = preferences;
            Interlocked.Increment(ref _settingsRevision);
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>
    /// Rejects bad input instead of silently clamping or dropping it, so "saved" always means the user
    /// got exactly what they asked for. Read-only keys the module itself reports are tolerated and
    /// ignored, which lets a full snapshot round-trip.
    /// </summary>
    private static void ValidateSettingValues(JsonObject values)
    {
        ValidateSettingKeys(values);

        if (values.ContainsKey("defaultHost"))
        {
            var defaultHost = ReadString(values, "defaultHost")?.Trim() ?? "";
            if (!RemoteCommandsStore.IsValidHost(defaultHost))
            {
                throw new ArgumentException("默认主机不能为空，且不能包含空白或以 - 开头。");
            }
        }

        if (values.ContainsKey("knownHosts"))
        {
            var knownHosts = ReadString(values, "knownHosts") ?? "";
            foreach (var line in knownHosts.Replace("\r\n", "\n").Split('\n'))
            {
                var candidate = line.Trim();
                if (candidate.Length > 0 && !RemoteCommandsStore.IsValidHost(candidate))
                {
                    throw new ArgumentException($"主机别名“{candidate}”无效：不能包含空白或以 - 开头。");
                }
            }
        }

        if (values.ContainsKey("historyRetention"))
        {
            var retention = ReadInt(values, "historyRetention")
                ?? throw new ArgumentException("历史条数上限必须是 10 到 5000 之间的整数。");
            if (retention is < 10 or > 5000)
            {
                throw new ArgumentException("历史条数上限必须在 10 到 5000 之间。");
            }
        }

        if (values.ContainsKey("condaExecutable"))
        {
            var executable = ReadString(values, "condaExecutable")?.Trim() ?? "";
            if (executable.Length == 0 || executable.Contains('\n') || executable.Contains('\r'))
            {
                throw new ArgumentException("CONDA_EXE 路径不能为空，也不能包含换行。");
            }
        }

        if (values.ContainsKey("commandTimeoutMinutes"))
        {
            var timeout = ReadInt(values, "commandTimeoutMinutes")
                ?? throw new ArgumentException("单次执行超时必须是 1 到 1440 之间的整数分钟。");
            if (timeout is < 1 or > 1440)
            {
                throw new ArgumentException("单次执行超时必须在 1 到 1440 分钟之间。");
            }
        }
    }

    private static void ValidateSettingKeys(JsonObject values)
    {
        foreach (var key in values.Select(pair => pair.Key))
        {
            if (ReadOnlySettingKeys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (!EditableSettingKeys.Contains(key, StringComparer.Ordinal))
            {
                throw new ArgumentException($"不支持通过设置接口修改“{key}”；凭据请使用主机命令写入系统凭据库。");
            }
        }
    }

    // ---------------------------------------------------------------- command bodies

    private CommandExecutionResult Catalog(CommandRequest request)
    {
        LoadCommands();
        return Succeeded(request, BuildCatalogJson().ToJsonString(IndentedJson));
    }

    /// <summary>
    /// Saves the shared commands.yaml the phone page edited.
    ///
    /// The command takes only the document text: the target is always the module's canonical
    /// <c>&lt;dataRoot&gt;/commands.yaml</c> through <see cref="RemoteCommandsStore.SaveCommands"/>
    /// (atomic write, same validator the desktop uses). There is deliberately no path argument, and an
    /// invalid document leaves both the file and the in-memory catalog exactly as they were.
    /// </summary>
    private async Task<CommandExecutionResult> SaveCatalogAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var content = ReadString(request.Args ?? new JsonObject(), RemoteCommandsAndroidOptions.ArgumentContent);
        if (content is null)
        {
            return SaveRejected(request, "请提供 commands.yaml 内容（content）。", contentLength: 0);
        }

        if (!RemoteCommandsYaml.TryValidate(content, out var validationError))
        {
            return SaveRejected(
                request,
                validationError ?? "commands.yaml 内容无效。",
                content.Length);
        }

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Store.SaveCommands(content);
            LoadCommands();
        }
        finally
        {
            _operations.Release();
        }

        Publish("catalog.saved", new JsonObject
        {
            ["title"] = "命令目录已保存",
            ["message"] = $"{_commands.Count} 条命令已写入 {RemoteCommandsAndroidOptions.CommandsFileName}。",
            ["commandCount"] = _commands.Count,
            ["contentLength"] = content.Length
        });

        var payload = BuildCatalogJson();
        payload["saved"] = true;
        payload["contentLength"] = content.Length;
        return Succeeded(request, payload.ToJsonString(IndentedJson));
    }

    /// <summary>
    /// A rejected save is reported as a failed command so no caller can mistake it for success, while
    /// the payload still carries the unchanged catalog and the validation message for an inline hint.
    /// </summary>
    private CommandExecutionResult SaveRejected(CommandRequest request, string error, int contentLength)
    {
        var payload = BuildCatalogJson();
        payload["saved"] = false;
        payload["contentLength"] = contentLength;
        payload["saveError"] = error;
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            payload.ToJsonString(IndentedJson),
            new MptRuntimeError(RemoteCommandsAndroidErrorCodes.ValidationFailed, error));
    }

    private JsonObject BuildCatalogJson()
    {
        var items = new JsonArray();
        foreach (var command in _commands)
        {
            items.Add(new JsonObject
            {
                ["id"] = command.Id,
                ["label"] = command.Label,
                ["command"] = command.Command,
                ["description"] = command.Description,
                ["type"] = command.Type,
                ["host"] = command.Host,
                ["input1Label"] = command.Input1Label,
                ["input1Placeholder"] = command.Input1Placeholder,
                ["input2Label"] = command.Input2Label,
                ["input2Placeholder"] = command.Input2Placeholder,
                ["showSecondInput"] = command.ShowSecondInput,
                ["usesRemoteHost"] = command.UsesRemoteHost
            });
        }

        return new JsonObject
        {
            ["commands"] = items,
            ["error"] = _commandsError,
            ["commandsPath"] = Store.CommandsPath
        };
    }

    private async Task<CommandExecutionResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var args = request.Args ?? new JsonObject();
        var commandId = ReadString(args, RemoteCommandsAndroidOptions.ArgumentCommandId)?.Trim() ?? "";
        if (commandId.Length == 0)
        {
            return Failed(request, RemoteCommandsAndroidErrorCodes.ValidationFailed, "请提供要运行的命令 id。");
        }

        LoadCommands();
        var command = _commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, commandId, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return Failed(
                request,
                RemoteCommandsAndroidErrorCodes.NotFound,
                $"commands.yaml 中没有命令 '{commandId}'。");
        }

        var settings = Store.LoadSettings();
        var requestedHost = ReadString(args, RemoteCommandsAndroidOptions.ArgumentHost)?.Trim() ?? "";
        var hostArgument = !string.IsNullOrWhiteSpace(command.Host)
            ? command.Host
            : !string.IsNullOrWhiteSpace(requestedHost)
                ? requestedHost
                : settings.DefaultHost;
        var input1 = ReadString(args, RemoteCommandsAndroidOptions.ArgumentInput1) ?? "";
        var input2 = ReadString(args, RemoteCommandsAndroidOptions.ArgumentInput2) ?? "";
        var secondInput = ReadBool(args, RemoteCommandsAndroidOptions.ArgumentSecondInput);

        if (!command.UsesRemoteHost)
        {
            // py transforms never touch SSH; they stay local exactly like the desktop path.
            return await RunLocalTransformAsync(request, command, input1, cancellationToken).ConfigureAwait(false);
        }

        var outcome = await Runner.RunAsync(
            new RemoteCommandRunRequest(request.InvocationId, command, hostArgument, input1, input2, secondInput),
            cancellationToken).ConfigureAwait(false);

        await AppendHistoryAsync(command, hostArgument, input1, input2, secondInput, outcome.Output, settings)
            .ConfigureAwait(false);

        _lastRunState = outcome.State;
        _lastRunSummary = outcome.State switch
        {
            RemoteCommandRunStates.Succeeded => $"上次执行成功：{command.Label} @ {hostArgument} (exit 0)",
            RemoteCommandRunStates.Cancelled => $"上次执行已取消：{command.Label} @ {hostArgument}",
            RemoteCommandRunStates.HostKeyRequired => $"等待确认主机密钥：{outcome.Message}",
            _ => $"上次执行失败：{command.Label} @ {hostArgument} — {outcome.Message}"
        };

        var payload = new JsonObject
        {
            ["invocationId"] = request.InvocationId,
            ["commandId"] = command.Id,
            ["label"] = command.Label,
            ["state"] = outcome.State,
            ["message"] = outcome.Message,
            ["exitCode"] = outcome.ExitCode,
            ["host"] = hostArgument,
            ["alias"] = outcome.Alias,
            ["resolvedHost"] = outcome.ResolvedHost,
            ["output"] = outcome.Output,
            ["pendingHostKey"] = outcome.PendingHostKey is null
                ? null
                : new JsonObject
                {
                    ["host"] = outcome.ResolvedHost,
                    ["hostKeyName"] = outcome.PendingHostKey.HostKeyName,
                    ["keyLength"] = outcome.PendingHostKey.KeyLength,
                    ["fingerprint"] = outcome.PendingHostKey.FingerprintSha256
                },
            ["transport"] = "managed-ssh"
        };

        // The command result is the tool's product surface, so it must not be an error object the Shell
        // hides; the state tells the caller what happened.
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            outcome.State,
            string.Equals(outcome.State, RemoteCommandRunStates.Succeeded, StringComparison.Ordinal),
            payload.ToJsonString(IndentedJson),
            string.Equals(outcome.State, RemoteCommandRunStates.Succeeded, StringComparison.Ordinal)
                ? null
                : new MptRuntimeError(
                    outcome.State == RemoteCommandRunStates.HostKeyRequired
                        ? RemoteCommandsAndroidErrorCodes.PermissionRequired
                        : RemoteCommandsAndroidErrorCodes.RuntimeUnavailable,
                    outcome.Message));
    }

    private async Task<CommandExecutionResult> RunLocalTransformAsync(
        CommandRequest request,
        RemoteCommandDefinition command,
        string input1,
        CancellationToken cancellationToken)
    {
        if (!RemoteCommandsTextTransforms.IsKnownTool(command.Command))
        {
            return Failed(
                request,
                RemoteCommandsAndroidErrorCodes.NotFound,
                $"py 命令 '{command.Command}' 没有对应的本地实现。");
        }

        var output = RemoteCommandsTextTransforms.Apply(command.Command, input1);
        _lastRunState = RemoteCommandRunStates.Succeeded;
        _lastRunSummary = $"上次本地转换成功：{command.Label}";
        await AppendHistoryAsync(
            command,
            "",
            input1,
            "",
            false,
            output,
            Store.LoadSettings()).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Succeeded(request, new JsonObject
        {
            ["invocationId"] = request.InvocationId,
            ["commandId"] = command.Id,
            ["label"] = command.Label,
            ["state"] = RemoteCommandRunStates.Succeeded,
            ["exitCode"] = 0,
            ["host"] = "",
            ["output"] = output,
            ["transport"] = "local"
        }.ToJsonString(IndentedJson));
    }

    private async Task AppendHistoryAsync(
        RemoteCommandDefinition command,
        string host,
        string input1,
        string input2,
        bool secondInput,
        string output,
        RemoteCommandsSettings settings)
    {
        try
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                await Store.AppendHistoryAsync(
                    new RemoteCommandHistoryItem(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        command.Label,
                        command.Command,
                        command.Type,
                        host,
                        input1,
                        input2,
                        secondInput,
                        output),
                    settings.HistoryRetention).ConfigureAwait(false);
            }
            finally
            {
                _operations.Release();
            }
        }
        catch (Exception exception)
        {
            // History is a convenience; a failed append must not turn a finished run into an error.
            Trace.WriteLine($"Remote Commands (Android): history append failed: {exception.Message}");
        }
    }

    private CommandExecutionResult Cancel(CommandRequest request)
    {
        var invocationId = ReadString(request.Args ?? new JsonObject(), RemoteCommandsAndroidOptions.ArgumentInvocationId);
        var cancelled = Runner.Cancel(invocationId);
        return Succeeded(request, new JsonObject
        {
            ["cancelled"] = cancelled,
            ["activeInvocationId"] = Runner.ActiveInvocationId,
            ["stage"] = Runner.ActiveStage
        }.ToJsonString(IndentedJson));
    }

    private async Task<JsonObject> BuildStateJsonAsync(CancellationToken cancellationToken)
    {
        var settings = Store.LoadSettings();
        var hosts = new JsonArray();
        foreach (var host in HostCatalog.Hosts)
        {
            hosts.Add(new JsonObject
            {
                ["alias"] = host.Alias,
                ["host"] = host.Host,
                ["port"] = host.Port,
                ["username"] = host.Username,
                ["auth"] = host.AuthKind.ToString().ToLowerInvariant(),
                ["credentialConfigured"] = await SecretAccess.HasCredentialAsync(host, cancellationToken)
                    .ConfigureAwait(false)
            });
        }

        return new JsonObject
        {
            ["moduleId"] = Id,
            ["commandsPath"] = Store.CommandsPath,
            ["commandCount"] = _commands.Count,
            ["commandsError"] = _commandsError,
            ["defaultHost"] = settings.DefaultHost,
            ["knownHosts"] = settings.KnownHosts,
            ["historyRetention"] = settings.HistoryRetention,
            ["condaExecutable"] = _preferences.CondaExecutable,
            ["commandTimeoutMinutes"] = _preferences.CommandTimeoutMinutes,
            ["hosts"] = hosts,
            ["trustedHostKeys"] = BuildHostKeysJson()["keys"]?.DeepClone(),
            ["activeInvocationId"] = Runner.ActiveInvocationId,
            ["activeStage"] = Runner.ActiveStage,
            ["lastRunState"] = _lastRunState,
            ["lastRunSummary"] = _lastRunSummary,
            ["backgroundAvailable"] = _background is not null,
            ["transportAvailable"] = TransportOverride?.IsAvailable ?? SshNetTransport.IsSupported,
            ["dataDirectory"] = _dataRoot
        };
    }

    private async Task<JsonObject> BuildHostsJsonAsync(CancellationToken cancellationToken)
    {
        LoadCommands();
        var configured = HostCatalog.Hosts;
        var referenced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in _commands.Where(command => command.UsesRemoteHost))
        {
            referenced.Add(string.IsNullOrWhiteSpace(command.Host) ? settingsDefaultHost() : command.Host);
        }

        var hosts = new JsonArray();
        foreach (var host in configured)
        {
            hosts.Add(new JsonObject
            {
                ["alias"] = host.Alias,
                ["host"] = host.Host,
                ["port"] = host.Port,
                ["username"] = host.Username,
                ["auth"] = host.AuthKind.ToString().ToLowerInvariant(),
                ["credentialConfigured"] = await SecretAccess.HasCredentialAsync(host, cancellationToken)
                    .ConfigureAwait(false)
            });
        }

        var missing = new JsonArray();
        foreach (var alias in referenced.Where(alias => HostCatalog.Resolve(alias) is null))
        {
            missing.Add(alias);
        }

        return new JsonObject
        {
            ["hosts"] = hosts,
            ["missingAliases"] = missing,
            ["catalogPath"] = HostCatalog.Path,
            ["loadFailed"] = HostCatalog.LoadFailed
        };

        string settingsDefaultHost() => Store.LoadSettings().DefaultHost;
    }

    private async Task<CommandExecutionResult> AddHostAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var args = request.Args ?? new JsonObject();
        var alias = ReadString(args, RemoteCommandsAndroidOptions.ArgumentAlias)?.Trim() ?? "";
        var host = ReadString(args, RemoteCommandsAndroidOptions.ArgumentRealHost)?.Trim() ?? "";
        var username = ReadString(args, RemoteCommandsAndroidOptions.ArgumentUsername)?.Trim() ?? "";
        var port = ReadInt(args, RemoteCommandsAndroidOptions.ArgumentPort) ?? 22;
        var authKind = ReadString(args, RemoteCommandsAndroidOptions.ArgumentAuthKind)?.Trim().ToLowerInvariant() switch
        {
            "password" => SshCredentialKind.Password,
            "privatekey" or "private-key" or "key" or "" or null => SshCredentialKind.PrivateKey,
            var other => throw new ArgumentException($"不支持的认证方式 '{other}'，请使用 password 或 privateKey。")
        };

        if (!RemoteCommandsAndroidHostCatalog.TryValidate(alias, host, port, username, authKind, out var error, out var entry))
        {
            return Failed(request, RemoteCommandsAndroidErrorCodes.ValidationFailed, error);
        }

        var password = ReadString(args, RemoteCommandsAndroidOptions.ArgumentPassword);
        var privateKey = ReadString(args, RemoteCommandsAndroidOptions.ArgumentPrivateKey);
        var passphrase = ReadString(args, RemoteCommandsAndroidOptions.ArgumentPassphrase);
        var secretProvided = authKind == SshCredentialKind.Password
            ? !string.IsNullOrEmpty(password)
            : !string.IsNullOrWhiteSpace(privateKey);
        var hadCredential = await SecretAccess.HasCredentialAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!secretProvided && !hadCredential)
        {
            return Failed(
                request,
                RemoteCommandsAndroidErrorCodes.ValidationFailed,
                authKind == SshCredentialKind.Password
                    ? "请提供密码（只写入系统凭据库，不会保存到设置文件）。"
                    : "请提供 OpenSSH 私钥内容（只写入系统凭据库，不会保存到设置文件）。");
        }

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (authKind == SshCredentialKind.Password)
            {
                if (!string.IsNullOrEmpty(password))
                {
                    await SecretAccess.SavePasswordAsync(alias, password, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (!string.IsNullOrWhiteSpace(privateKey))
            {
                await SecretAccess.SavePrivateKeyAsync(alias, privateKey, passphrase, cancellationToken)
                    .ConfigureAwait(false);
            }

            HostCatalog.Upsert(entry);
        }
        finally
        {
            _operations.Release();
        }

        Publish("host.updated", new JsonObject
        {
            ["title"] = "已保存主机",
            ["message"] = $"{entry.Alias} → {entry.Username}@{entry.Host}:{entry.Port}",
            ["alias"] = entry.Alias,
            ["host"] = entry.Host
        });

        return Succeeded(request, (await BuildHostsJsonAsync(cancellationToken).ConfigureAwait(false))
            .ToJsonString(IndentedJson));
    }

    private async Task<CommandExecutionResult> RemoveHostAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var alias = ReadString(request.Args ?? new JsonObject(), RemoteCommandsAndroidOptions.ArgumentAlias)?.Trim() ?? "";
        if (alias.Length == 0)
        {
            return Failed(request, RemoteCommandsAndroidErrorCodes.ValidationFailed, "请提供要删除的主机别名。");
        }

        var entry = HostCatalog.Resolve(alias);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry is not null)
            {
                await SecretAccess.ClearAsync(entry.Alias, cancellationToken).ConfigureAwait(false);
                HostCatalog.Remove(entry.Alias);
                HostKeys.Revoke(entry.Host, entry.Port);
            }
        }
        finally
        {
            _operations.Release();
        }

        Publish("host.removed", new JsonObject
        {
            ["title"] = "已删除主机",
            ["message"] = alias,
            ["alias"] = alias
        });

        return Succeeded(request, (await BuildHostsJsonAsync(cancellationToken).ConfigureAwait(false))
            .ToJsonString(IndentedJson));
    }

    private JsonObject BuildHostKeysJson()
    {
        var keys = new JsonArray();
        foreach (var key in HostKeys.Keys)
        {
            keys.Add(new JsonObject
            {
                ["host"] = key.Host,
                ["port"] = key.Port,
                ["hostKeyName"] = key.HostKeyName,
                ["fingerprint"] = key.FingerprintSha256,
                ["addedAt"] = key.AddedAt
            });
        }

        return new JsonObject
        {
            ["keys"] = keys,
            ["storePath"] = HostKeys.Path
        };
    }

    /// <summary>
    /// Confirms one exact fingerprint. The caller must pass the fingerprint it displayed, so a
    /// "confirm" click can never trust a key the user did not see.
    /// </summary>
    private CommandExecutionResult AcceptHostKey(CommandRequest request)
    {
        var args = request.Args ?? new JsonObject();
        var host = ReadString(args, RemoteCommandsAndroidOptions.ArgumentRealHost)?.Trim()
                   ?? ReadString(args, RemoteCommandsAndroidOptions.ArgumentHost)?.Trim()
                   ?? "";
        var fingerprint = ReadString(args, RemoteCommandsAndroidOptions.ArgumentFingerprint)?.Trim() ?? "";
        var port = ReadInt(args, RemoteCommandsAndroidOptions.ArgumentPort) ?? 22;
        var hostKeyName = ReadString(args, "hostKeyName")?.Trim() ?? "";
        if (host.Length == 0 || fingerprint.Length == 0)
        {
            return Failed(
                request,
                RemoteCommandsAndroidErrorCodes.ValidationFailed,
                "确认主机密钥需要提供真实主机、端口与要确认的 SHA256 指纹。");
        }

        var entry = HostCatalog.Resolve(host);
        var resolvedHost = entry?.Host ?? host;
        HostKeys.Remember(
            resolvedHost,
            port,
            new SshHostKeyObservation(hostKeyName, fingerprint, 0));

        Publish("host-key.accepted", new JsonObject
        {
            ["title"] = "已确认主机密钥",
            ["message"] = $"{resolvedHost}:{port} {fingerprint}",
            ["host"] = resolvedHost,
            ["port"] = port,
            ["fingerprint"] = fingerprint
        });

        return Succeeded(request, BuildHostKeysJson().ToJsonString(IndentedJson));
    }

    private CommandExecutionResult RevokeHostKey(CommandRequest request)
    {
        var args = request.Args ?? new JsonObject();
        var host = ReadString(args, RemoteCommandsAndroidOptions.ArgumentRealHost)?.Trim()
                   ?? ReadString(args, RemoteCommandsAndroidOptions.ArgumentHost)?.Trim()
                   ?? "";
        var port = ReadInt(args, RemoteCommandsAndroidOptions.ArgumentPort) ?? 22;
        if (host.Length == 0)
        {
            return Failed(request, RemoteCommandsAndroidErrorCodes.ValidationFailed, "请提供要撤销的主机。");
        }

        var entry = HostCatalog.Resolve(host);
        var removed = HostKeys.Revoke(entry?.Host ?? host, port);
        Publish("host-key.revoked", new JsonObject
        {
            ["title"] = "已撤销主机密钥",
            ["message"] = $"{entry?.Host ?? host}:{port}",
            ["removed"] = removed
        });
        return Succeeded(request, BuildHostKeysJson().ToJsonString(IndentedJson));
    }

    private CommandExecutionResult HistorySummary(CommandRequest request)
    {
        var items = Store.LoadHistory();
        var latest = items.FirstOrDefault();
        return Succeeded(request, new JsonObject
        {
            ["count"] = items.Count,
            ["latestTimestamp"] = latest?.Timestamp ?? "",
            ["latestLabel"] = latest?.Label ?? "",
            ["latestHost"] = latest?.Host ?? "",
            ["storePath"] = Store.CommandsPath
        }.ToJsonString(IndentedJson));
    }

    private async Task<CommandExecutionResult> ClearHistoryAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Store.ClearHistory();
        }
        finally
        {
            _operations.Release();
        }

        Publish("history.cleared", new JsonObject
        {
            ["title"] = "历史已清空",
            ["message"] = "本机保存的远程命令执行历史已删除。"
        });
        return Succeeded(request, new JsonObject { ["count"] = 0 }.ToJsonString(IndentedJson));
    }

    private CommandExecutionResult Transform(CommandRequest request)
    {
        var args = request.Args ?? new JsonObject();
        var tool = ReadString(args, "tool")?.Trim() ?? "";
        if (!RemoteCommandsTextTransforms.IsKnownTool(tool))
        {
            return Failed(request, RemoteCommandsAndroidErrorCodes.NotFound, $"未知的本地转换 '{tool}'。");
        }

        var output = RemoteCommandsTextTransforms.Apply(tool, ReadString(args, RemoteCommandsAndroidOptions.ArgumentInput1) ?? "");
        return Succeeded(request, new JsonObject
        {
            ["tool"] = tool,
            ["output"] = output,
            ["transport"] = "local"
        }.ToJsonString(IndentedJson));
    }

    // ---------------------------------------------------------------- surfaces

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new(
                $"{RemoteCommandsAndroidOptions.ModuleId}.workspace",
                "detail-page",
                RemoteCommandsAndroidOptions.DisplayName,
                new JsonObject
                {
                    ["surfaceAssembly"] = RemoteCommandsAndroidOptions.SurfaceAssemblyFileName,
                    ["surfaceType"] = RemoteCommandsAndroidOptions.SurfaceTypeName
                })
        ];
        return ValueTask.FromResult(surfaces);
    }

    // ---------------------------------------------------------------- helpers

    private void LoadCommands()
    {
        try
        {
            _commands = Store.LoadCommands();
            _commandsError = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _commands = [];
            _commandsError = $"无法读取 {RemoteCommandsAndroidOptions.CommandsFileName}：{exception.Message}";
        }
    }

    private static string ClassifyError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => RemoteCommandsAndroidErrorCodes.PermissionRequired,
        InvalidDataException or ArgumentException => RemoteCommandsAndroidErrorCodes.ValidationFailed,
        NotSupportedException => RemoteCommandsAndroidErrorCodes.RuntimeUnavailable,
        _ => RemoteCommandsAndroidErrorCodes.RuntimeUnavailable
    };

    private static CommandExecutionResult Succeeded(CommandRequest request, string output) =>
        new(request.InvocationId, request.CommandId, "succeeded", true, output);

    private static CommandExecutionResult Failed(
        CommandRequest request,
        string code,
        string message,
        bool retryable = false) =>
        new(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            "",
            new MptRuntimeError(code, message, retryable));

    private static string? ReadString(JsonObject values, string key) =>
        values.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>
    /// Reads an integer argument.
    ///
    /// The value does not always arrive as a JSON int: HostControl carries command arguments in a
    /// protobuf <c>Struct</c>, whose only numeric type is <c>double</c>, so a phone-side
    /// <c>JsonObject.Int32</c> reaches the module as a double-backed <see cref="JsonValue"/> (and a
    /// future host normalization to <c>long</c> would too). Reading only <c>int</c> therefore turned
    /// every integer argument into "not an integer" on a real device. int, long, integral
    /// double/decimal and numeric strings are all accepted; a fractional number is rejected instead of
    /// being rounded.
    /// </summary>
    private static int? ReadInt(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<long>(out var wide))
        {
            return wide is >= int.MinValue and <= int.MaxValue ? (int)wide : null;
        }

        if (value.TryGetValue<double>(out var floating))
        {
            return Math.Floor(floating) == floating && floating is >= int.MinValue and <= int.MaxValue
                ? (int)floating
                : null;
        }

        if (value.TryGetValue<decimal>(out var precise))
        {
            return decimal.Floor(precise) == precise && precise is >= int.MinValue and <= int.MaxValue
                ? (int)precise
                : null;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }

    private static bool ReadBool(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed;
    }

    /// <summary>
    /// Fallback transport for builds where SSH.NET could not be loaded. It reports the problem instead
    /// of pretending a connection happened.
    /// </summary>
    private sealed class UnavailableSshTransport : ISshTransport
    {
        public bool IsAvailable => false;

        public string UnavailableReason =>
            "当前构建没有加载托管 SSH 客户端（SSH.NET），无法执行远程命令。请重新安装包含该依赖的模块包。";

        public Task<ISshSession> ConnectAsync(
            SshConnectionRequest request,
            SshCredential credential,
            string? trustedFingerprint,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(UnavailableReason);
    }

    private sealed class StreamSink
    {
        public Channel<CommandExecutionEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<CommandExecutionEvent>(
                new UnboundedChannelOptions { SingleReader = true });

        public long Sequence;
    }

    /// <summary>
    /// Coalesces output lines so one chatty command cannot publish one event per line, while keeping
    /// ordering: any non-output event flushes what is pending first. The timer is armed only while
    /// lines are actually pending, so an idle module never wakes up on its own.
    /// </summary>
    private sealed class OutputBatcher : IDisposable
    {
        private const int MaxLinesPerEvent = 40;
        private const int MaxCharactersPerEvent = 4096;

        private readonly Action<string, IReadOnlyList<string>> _flush;
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly Timer _timer;
        private string _invocationId = "";

        public OutputBatcher(Action<string, IReadOnlyList<string>> flush)
        {
            _flush = flush;
            _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Add(string invocationId, string line)
        {
            List<string>? ready = null;
            string? readyInvocation = null;
            lock (_gate)
            {
                if (!string.Equals(_invocationId, invocationId, StringComparison.Ordinal) && _lines.Count > 0)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    ready = [.. _lines];
                    readyInvocation = _invocationId;
                    _lines.Clear();
                }

                _invocationId = invocationId;
                _lines.Add(line);
                if (_lines.Count >= MaxLinesPerEvent || _lines.Sum(item => item.Length) >= MaxCharactersPerEvent)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    ready = [.. _lines];
                    readyInvocation = _invocationId;
                    _lines.Clear();
                }
                else if (_lines.Count == 1)
                {
                    _timer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
                }
            }

            if (ready is not null && readyInvocation is not null)
            {
                _flush(readyInvocation, ready);
            }
        }

        public void FlushIfPending(string eventType)
        {
            if (!string.Equals(eventType, "command.output", StringComparison.Ordinal))
            {
                Flush();
            }
        }

        public void Flush()
        {
            List<string>? ready = null;
            string? invocation = null;
            lock (_gate)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                if (_lines.Count > 0)
                {
                    ready = [.. _lines];
                    invocation = _invocationId;
                    _lines.Clear();
                }
            }

            if (ready is not null && invocation is not null)
            {
                _flush(invocation, ready);
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}
