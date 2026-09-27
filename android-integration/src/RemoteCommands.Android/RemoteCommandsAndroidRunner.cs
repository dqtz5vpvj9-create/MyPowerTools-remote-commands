using System.Text;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Android;

/// <summary>Everything one remote command run needs, already resolved by the module.</summary>
internal sealed record RemoteCommandRunRequest(
    string InvocationId,
    RemoteCommandDefinition Command,
    string HostArgument,
    string Input1,
    string Input2,
    bool SecondInput);

/// <summary>Terminal state of one run. <c>host-key-required</c> is not a failure of the user's command.</summary>
internal static class RemoteCommandRunStates
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string HostKeyRequired = "host-key-required";
}

internal sealed record RemoteCommandRunOutcome(
    string State,
    string Message,
    string Output,
    int? ExitCode,
    string HostArgument,
    string ResolvedHost,
    string Alias,
    SshHostKeyObservation? PendingHostKey);

/// <summary>
/// Runs one configured command over SSH. Rules this type exists to enforce:
/// <list type="bullet">
///   <item>a run only happens because a user asked for it - nothing here connects on module start,
///   module enable, a timer or a settings change;</item>
///   <item>an alias is never guessed into a host: without a catalog entry the run fails with the
///   exact fields the user has to fill in;</item>
///   <item>host key verification is mandatory and first-use/changed keys stop before authentication
///   and surface the fingerprint for explicit confirmation;</item>
///   <item>a <c>background.activity</c> lease is held only while a run is in flight, and cancel or
///   disable kills the command, drops the connection and releases the lease.</item>
/// </list>
/// </summary>
internal sealed class RemoteCommandsAndroidRunner
{
    private readonly ISshTransport _transport;
    private readonly RemoteCommandsAndroidHostCatalog _catalog;
    private readonly RemoteCommandsAndroidHostKeyStore _hostKeys;
    private readonly RemoteCommandsAndroidSecrets _secrets;
    private readonly IBackgroundActivityService? _background;
    private readonly Func<RemoteCommandsAndroidPreferences> _preferences;
    private readonly Action<string, JsonObject> _publish;
    private readonly Action<string> _log;

    private ActiveRun? _activeRun;

    public RemoteCommandsAndroidRunner(
        ISshTransport transport,
        RemoteCommandsAndroidHostCatalog catalog,
        RemoteCommandsAndroidHostKeyStore hostKeys,
        RemoteCommandsAndroidSecrets secrets,
        IBackgroundActivityService? background,
        Func<RemoteCommandsAndroidPreferences> preferences,
        Action<string, JsonObject> publish,
        Action<string>? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _hostKeys = hostKeys ?? throw new ArgumentNullException(nameof(hostKeys));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _background = background;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _log = log ?? (_ => { });
    }

    /// <summary>Test seam: makes the remote temporary file names deterministic.</summary>
    internal Func<string> RunIdFactory { get; set; } = () => Guid.NewGuid().ToString("N")[..8];

    internal bool IsRunning => Volatile.Read(ref _activeRun) is not null;

    internal string ActiveInvocationId => Volatile.Read(ref _activeRun)?.InvocationId ?? "";

    internal string ActiveStage => Volatile.Read(ref _activeRun)?.Stage ?? "idle";

    /// <summary>Cancels the active run (or the named one). Returns false when nothing was running.</summary>
    public bool Cancel(string? invocationId)
    {
        var active = Volatile.Read(ref _activeRun);
        if (active is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(invocationId) &&
            !string.Equals(active.InvocationId, invocationId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        active.Stage = "cancelling";
        try
        {
            active.Session?.RequestCancel();
        }
        catch (Exception exception)
        {
            _log("cancel-session: " + exception.Message);
        }

        try
        {
            active.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished.
        }

        return true;
    }

    public async Task<RemoteCommandRunOutcome> RunAsync(
        RemoteCommandRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _activeRun) is not null)
        {
            return Failure(request, "已有远程命令正在执行，请先取消或等待完成。");
        }

        var entry = _catalog.Resolve(request.HostArgument);
        if (entry is null)
        {
            var hint = string.IsNullOrWhiteSpace(request.HostArgument)
                ? "请先选择主机。"
                : $"手机上没有配置主机“{request.HostArgument}”的真实地址。";
            return Failure(
                request,
                hint + " Android 没有 ~/.ssh/config，请在“主机”里填写：别名（与命令中的主机名一致）、真实主机名或 IP、端口、SSH 用户名，以及密码或私钥。");
        }

        SshCredential credential;
        try
        {
            credential = await _secrets.LoadAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Failure(request, exception.Message);
        }

        if (!_transport.IsAvailable)
        {
            return Failure(request, _transport.UnavailableReason.Length > 0
                ? _transport.UnavailableReason
                : "当前构建没有可用的 SSH 传输。");
        }

        var preferences = _preferences();
        var trustedFingerprint = _hostKeys.FingerprintFor(entry.Host, entry.Port);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveRun(request.InvocationId, cancellation);
        if (Interlocked.CompareExchange(ref _activeRun, active, null) is not null)
        {
            cancellation.Dispose();
            return Failure(request, "已有远程命令正在执行，请先取消或等待完成。");
        }

        var output = new OutputCollector(RemoteCommandsAndroidOptions.MaxOutputLength);
        IDisposable? lease = null;
        ISshSession? session = null;
        var remotePaths = new List<string>();
        try
        {
            _publish("run.started", new JsonObject
            {
                ["invocationId"] = request.InvocationId,
                ["commandId"] = request.Command.Id,
                ["label"] = request.Command.Label,
                ["host"] = request.HostArgument,
                ["alias"] = entry.Alias,
                ["resolvedHost"] = entry.Host
            });

            if (_background is not null)
            {
                try
                {
                    lease = await _background.BeginAsync(
                        RemoteCommandsAndroidOptions.ModuleId,
                        $"{RemoteCommandsAndroidOptions.BackgroundActivityTitle}：{request.Command.Label}",
                        waitingForPeers: false,
                        cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A missing notification permission must not lose the run the user asked for; the
                    // user is told that background protection is unavailable, and the run continues in
                    // the foreground.
                    _log("background-lease: " + exception.Message);
                }
            }

            active.Stage = "connecting";
            session = await _transport.ConnectAsync(
                new SshConnectionRequest(
                    entry.Alias,
                    entry.Host,
                    entry.Port,
                    entry.Username,
                    preferences.ConnectTimeout,
                    preferences.CommandTimeout),
                credential,
                trustedFingerprint,
                cancellation.Token).ConfigureAwait(false);
            active.Session = session;

            var runId = RunIdFactory();
            var file1 = $"{RemoteCommandsAndroidOptions.RemoteUploadDirectory}/mpt-remote-commands-{runId}.input1";
            var file2 = $"{RemoteCommandsAndroidOptions.RemoteUploadDirectory}/mpt-remote-commands-{runId}.input2";
            remotePaths.Add(file1);
            remotePaths.Add(file2);

            active.Stage = "uploading";
            _publish("run.stage", new JsonObject { ["invocationId"] = request.InvocationId, ["stage"] = "uploading" });
            output.Append("Uploading input files...");
            await session.UploadAsync(file1, request.Input1 ?? "", cancellation.Token).ConfigureAwait(false);
            await session.UploadAsync(file2, request.Input2 ?? "", cancellation.Token).ConfigureAwait(false);

            cancellation.Token.ThrowIfCancellationRequested();
            active.Stage = "running";
            _publish("run.stage", new JsonObject { ["invocationId"] = request.InvocationId, ["stage"] = "running" });
            output.Append("Executing remote command...");
            var commandLine = BuildCommandLine(request.Command.Command, preferences.CondaExecutable, file1, file2);
            var exitCode = await session.ExecuteAsync(
                commandLine,
                line =>
                {
                    output.Append(line);
                    _publish("command.output", new JsonObject
                    {
                        ["invocationId"] = request.InvocationId,
                        ["line"] = line
                    });
                },
                cancellation.Token).ConfigureAwait(false);

            output.Append(exitCode == 0 ? "Execution complete" : "Execution failed");
            var state = exitCode == 0 ? RemoteCommandRunStates.Succeeded : RemoteCommandRunStates.Failed;
            PublishFinished(request, state, exitCode, output);
            return new RemoteCommandRunOutcome(
                state,
                exitCode == 0 ? "执行完成" : $"远端命令返回退出码 {exitCode}",
                output.ToString(),
                exitCode,
                request.HostArgument,
                entry.Host,
                entry.Alias,
                null);
        }
        catch (SshHostKeyMismatchException exception)
        {
            output.Append(exception.Message);
            _publish("host-key.pending", new JsonObject
            {
                ["invocationId"] = request.InvocationId,
                ["host"] = entry.Host,
                ["port"] = entry.Port,
                ["alias"] = entry.Alias,
                ["hostKeyName"] = exception.Observation.HostKeyName,
                ["keyLength"] = exception.Observation.KeyLength,
                ["fingerprint"] = exception.Observation.FingerprintSha256,
                ["firstUse"] = exception.IsFirstUse,
                ["message"] = exception.Message
            });
            PublishFinished(request, RemoteCommandRunStates.HostKeyRequired, null, output);
            return new RemoteCommandRunOutcome(
                RemoteCommandRunStates.HostKeyRequired,
                exception.Message,
                output.ToString(),
                null,
                request.HostArgument,
                entry.Host,
                entry.Alias,
                exception.Observation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            output.Append("Cancelled");
            Cancel(session);
            PublishFinished(request, RemoteCommandRunStates.Cancelled, null, output);
            return new RemoteCommandRunOutcome(
                RemoteCommandRunStates.Cancelled,
                "已取消",
                output.ToString(),
                null,
                request.HostArgument,
                entry.Host,
                entry.Alias,
                null);
        }
        catch (Exception exception)
        {
            output.Append(exception.Message);
            PublishFinished(request, RemoteCommandRunStates.Failed, null, output);
            return new RemoteCommandRunOutcome(
                RemoteCommandRunStates.Failed,
                exception.Message,
                output.ToString(),
                null,
                request.HostArgument,
                entry.Host,
                entry.Alias,
                null);
        }
        finally
        {
            if (session is not null)
            {
                if (cancellation.IsCancellationRequested)
                {
                    Cancel(session);
                }
                else if (remotePaths.Count > 0)
                {
                    active.Stage = "cleanup";
                    try
                    {
                        await session.DeleteFilesAsync(remotePaths, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        // Best effort, exactly like the desktop executor's trailing rm -f.
                        _log("cleanup: " + exception.Message);
                    }
                }

                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _log("dispose-session: " + exception.Message);
                }

                active.Session = null;
            }

            lease?.Dispose();
            Interlocked.CompareExchange(ref _activeRun, null, active);
            active.Stage = "idle";
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Same remote command line contract as the desktop executor: the configured command keeps its
    /// own arguments, and the two uploaded files are appended as <c>--file1/--file2</c>.
    /// </summary>
    internal static string BuildCommandLine(string command, string condaExecutable, string file1, string file2)
    {
        var prefix = string.IsNullOrWhiteSpace(condaExecutable)
            ? ""
            : $"CONDA_EXE={condaExecutable.Trim()} ";
        return $"{prefix}{command} --file1 {Quote(file1)} --file2 {Quote(file2)}";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static void Cancel(ISshSession? session)
    {
        try
        {
            session?.RequestCancel();
        }
        catch (Exception)
        {
            // Session teardown is best effort.
        }
    }

    private void PublishFinished(
        RemoteCommandRunRequest request,
        string state,
        int? exitCode,
        OutputCollector output)
    {
        _publish("run.finished", new JsonObject
        {
            ["invocationId"] = request.InvocationId,
            ["commandId"] = request.Command.Id,
            ["state"] = state,
            ["exitCode"] = exitCode,
            ["outputLength"] = output.Length
        });
    }

    private static RemoteCommandRunOutcome Failure(RemoteCommandRunRequest request, string message) =>
        new(
            RemoteCommandRunStates.Failed,
            message,
            message,
            null,
            request.HostArgument,
            "",
            "",
            null);

    private sealed class ActiveRun(string invocationId, CancellationTokenSource cancellation)
    {
        public string InvocationId { get; } = invocationId;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public ISshSession? Session { get; set; }

        public volatile string Stage = "starting";
    }

    /// <summary>
    /// Keeps the desktop executor's 512 KiB tail behaviour: the visible output stays bounded while the
    /// newest lines are always present.
    /// </summary>
    private sealed class OutputCollector(int maxLength)
    {
        private readonly StringBuilder _builder = new();
        private bool _truncated;

        public int Length => _builder.Length;

        public void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            if (!_truncated)
            {
                _builder.Append(line).Append('\n');
                if (_builder.Length > maxLength)
                {
                    var tail = _builder.ToString()[^maxLength..];
                    _builder.Clear();
                    _builder.Append("... (output truncated) ...\n").Append(tail);
                    _truncated = true;
                }

                return;
            }

            _builder.Append(line).Append('\n');
            if (_builder.Length > maxLength)
            {
                _builder.Remove(0, _builder.Length - maxLength);
            }
        }

        public override string ToString() => _builder.ToString();
    }
}
