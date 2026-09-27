using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Android.Tests;

/// <summary>In-memory credential store; records what the module asked it to keep.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Values => _values;

    public Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        var reference = SecretReference.Create(moduleId, name);
        _values[reference.Uri] = secret;
        return Task.FromResult(reference);
    }

    public Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(reference.Uri, out var value) ? value : null);

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        _values.Remove(reference.Uri);
        return Task.CompletedTask;
    }

    /// <summary>Drops every stored value, used to simulate a host whose credential was never saved.</summary>
    public void Clear() => _values.Clear();
}

/// <summary>Records foreground-activity leases so a test can assert they are released.</summary>
internal sealed class FakeBackgroundActivity : IBackgroundActivityService
{
    private int _active;

    public int BeginCount { get; private set; }

    public int DisposeCount { get; private set; }

    public int ActiveCount => Volatile.Read(ref _active);

    public List<string> Titles { get; } = [];

    public Exception? FailWith { get; set; }

    public Task<IDisposable> BeginAsync(string moduleId, string title, bool waitingForPeers, CancellationToken cancellationToken)
    {
        if (FailWith is not null)
        {
            return Task.FromException<IDisposable>(FailWith);
        }

        BeginCount++;
        Titles.Add(title);
        Interlocked.Increment(ref _active);
        return Task.FromResult<IDisposable>(new Lease(this));
    }

    private sealed class Lease(FakeBackgroundActivity owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.DisposeCount++;
                Interlocked.Decrement(ref owner._active);
            }
        }
    }
}

/// <summary>
/// Scripted SSH transport. It performs the same host key policy contract as the production transport
/// (reject unless the observed fingerprint equals the confirmed one) and records every call, so the
/// module's behaviour can be verified without a socket.
/// </summary>
internal sealed class FakeSshTransport : ISshTransport
{
    public bool IsAvailable { get; set; } = true;

    public string UnavailableReason { get; set; } = "";

    public SshHostKeyObservation Observation { get; set; } =
        new("ssh-ed25519", "SHA256:AAAABBBBCCCCDDDD", 256);

    public int ConnectCount { get; private set; }

    public string? LastTrustedFingerprint { get; private set; }

    public SshConnectionRequest? LastRequest { get; private set; }

    public SshCredential? LastCredential { get; private set; }

    public FakeSshSession Session { get; } = new();

    /// <summary>When set, replaces the default connect behaviour entirely.</summary>
    public Func<SshConnectionRequest, SshCredential, string?, CancellationToken, Task<ISshSession>>? ConnectOverride { get; set; }

    public Task<ISshSession> ConnectAsync(
        SshConnectionRequest request,
        SshCredential credential,
        string? trustedFingerprint,
        CancellationToken cancellationToken)
    {
        ConnectCount++;
        LastRequest = request;
        LastCredential = credential;
        LastTrustedFingerprint = trustedFingerprint;

        if (ConnectOverride is not null)
        {
            return ConnectOverride(request, credential, trustedFingerprint, cancellationToken);
        }

        if (!string.Equals(trustedFingerprint, Observation.FingerprintSha256, StringComparison.Ordinal))
        {
            return Task.FromException<ISshSession>(new SshHostKeyMismatchException(
                Observation,
                trustedFingerprint,
                trustedFingerprint is null
                    ? $"{request.Host}:{request.Port} 的主机密钥尚未确认。"
                    : $"{request.Host}:{request.Port} 的主机密钥已变化。"));
        }

        return Task.FromResult<ISshSession>(Session);
    }
}

/// <summary>Scripted session that records uploads, the command line, cleanup and cancellation.</summary>
internal sealed class FakeSshSession : ISshSession
{
    public List<(string Path, string Content)> Uploads { get; } = [];

    public List<string> DeletedPaths { get; } = [];

    public string? CommandLine { get; private set; }

    public int ExecuteCount { get; private set; }

    public bool Disposed { get; private set; }

    public bool CancelRequested { get; private set; }

    public int ExitCode { get; set; }

    public IReadOnlyList<string> OutputLines { get; set; } = ["remote line 1", "remote line 2"];

    public Exception? UploadFailure { get; set; }

    /// <summary>When set, replaces the default execution (used for cancellation tests).</summary>
    public Func<Action<string>, CancellationToken, Task<int>>? ExecuteOverride { get; set; }

    public Task UploadAsync(string remotePath, string content, CancellationToken cancellationToken)
    {
        if (UploadFailure is not null)
        {
            return Task.FromException(UploadFailure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Uploads.Add((remotePath, content));
        return Task.CompletedTask;
    }

    public async Task<int> ExecuteAsync(string commandLine, Action<string> onOutput, CancellationToken cancellationToken)
    {
        CommandLine = commandLine;
        ExecuteCount++;
        if (ExecuteOverride is not null)
        {
            return await ExecuteOverride(onOutput, cancellationToken).ConfigureAwait(false);
        }

        foreach (var line in OutputLines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onOutput(line);
        }

        return ExitCode;
    }

    public Task DeleteFilesAsync(IReadOnlyList<string> remotePaths, CancellationToken cancellationToken)
    {
        DeletedPaths.AddRange(remotePaths);
        return Task.CompletedTask;
    }

    public void RequestCancel() => CancelRequested = true;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Builds a fully wired module against fakes and a private data directory.</summary>
internal sealed class RemoteCommandsTestHost : IAsyncDisposable
{
    private RemoteCommandsTestHost(
        RemoteCommandsAndroidModule module,
        string dataRoot,
        FakeSshTransport transport,
        FakeSecretStore secrets,
        FakeBackgroundActivity background)
    {
        Module = module;
        DataRoot = dataRoot;
        Transport = transport;
        Secrets = secrets;
        Background = background;
    }

    public RemoteCommandsAndroidModule Module { get; }

    public string DataRoot { get; }

    public FakeSshTransport Transport { get; }

    public FakeSecretStore Secrets { get; }

    public FakeBackgroundActivity Background { get; }

    public RemoteCommandsStore Store => new(DataRoot);

    public static async Task<RemoteCommandsTestHost> CreateAsync(
        string? commandsYaml = null,
        string? settingsJson = null,
        string platform = "android-arm64",
        bool withBackground = true)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "modules", RemoteCommandsAndroidOptions.ModuleId, "data");
        Directory.CreateDirectory(dataDirectory);

        var dataRoot = RemoteCommandsAndroidPaths.ResolveDataRoot(dataDirectory);
        Directory.CreateDirectory(dataRoot);
        if (commandsYaml is not null)
        {
            File.WriteAllText(RemoteCommandsAndroidPaths.CommandsPath(dataRoot), commandsYaml);
        }

        if (settingsJson is not null)
        {
            File.WriteAllText(RemoteCommandsAndroidPaths.SettingsPath(dataRoot), settingsJson);
        }

        var secrets = new FakeSecretStore();
        var background = new FakeBackgroundActivity();
        var transport = new FakeSshTransport();
        var providers = new Dictionary<string, object> { ["secret.store"] = secrets };
        if (withBackground)
        {
            providers["background.activity"] = background;
        }

        var module = new RemoteCommandsAndroidModule { TransportOverride = transport };
        var context = new ModuleContext(
            HostVersion: "0.4.0",
            ProtocolVersion: "1.0",
            PackageId: RemoteCommandsAndroidOptions.PackageId,
            ModuleId: RemoteCommandsAndroidOptions.ModuleId,
            DataDirectory: dataDirectory,
            CacheDirectory: Path.Combine(root, "cache"),
            LogDirectory: Path.Combine(root, "logs"),
            Platform: platform,
            GrantedCapabilities: ["secret.store", "background.activity"],
            CapabilityProviders: providers);
        await module.InitializeAsync(context, CancellationToken.None).ConfigureAwait(false);
        return new RemoteCommandsTestHost(module, dataRoot, transport, secrets, background);
    }

    /// <summary>Adds one servable host the way the phone UI does: catalog entry plus secret store value.</summary>
    public async Task AddHostAsync(
        string alias = "r743",
        string host = "192.168.22.24",
        string username = "lixr",
        int port = 22,
        string? password = null,
        string? privateKey = null,
        string? passphrase = null)
    {
        // A host entry always needs a credential; callers that only care about the mapping get a
        // throwaway password instead of an invalid "no credential" entry.
        if (password is null && privateKey is null)
        {
            password = "test-password";
        }

        var args = new JsonObject
        {
            ["alias"] = alias,
            ["hostName"] = host,
            ["port"] = port,
            ["username"] = username,
            ["auth"] = password is not null ? "password" : "privateKey"
        };
        if (password is not null)
        {
            args["password"] = password;
        }

        if (privateKey is not null)
        {
            args["privateKey"] = privateKey;
        }

        if (passphrase is not null)
        {
            args["passphrase"] = passphrase;
        }

        var result = await Module.ExecuteCommandAsync(
            new CommandRequest("invoke-host-add", RemoteCommandsAndroidOptions.CommandHostAdd, args),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(result.Success, result.Error?.Message ?? result.Output);
    }

    /// <summary>Confirms the fingerprint the module reported as pending.</summary>
    public async Task AcceptHostKeyAsync(string host, int port, string fingerprint, string hostKeyName = "ssh-ed25519")
    {
        var result = await Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-key-accept",
                RemoteCommandsAndroidOptions.CommandHostKeyAccept,
                new JsonObject
                {
                    ["hostName"] = host,
                    ["port"] = port,
                    ["fingerprint"] = fingerprint,
                    ["hostKeyName"] = hostKeyName
                }),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(result.Success, result.Error?.Message ?? result.Output);
    }

    public Task<CommandExecutionResult> RunAsync(
        string commandId,
        string host = "r743",
        string input1 = "first input",
        string input2 = "second input",
        bool secondInput = true,
        CancellationToken cancellationToken = default,
        string invocationId = "invoke-run") =>
        Module.ExecuteCommandAsync(
            new CommandRequest(
                invocationId,
                RemoteCommandsAndroidOptions.CommandRun,
                new JsonObject
                {
                    ["commandId"] = commandId,
                    ["host"] = host,
                    ["input1"] = input1,
                    ["input2"] = input2,
                    ["secondInput"] = secondInput
                }),
            cancellationToken).AsTask();

    public static JsonObject Parse(CommandExecutionResult result) =>
        JsonNode.Parse(result.Output) as JsonObject ?? new JsonObject();

    public async ValueTask DisposeAsync()
    {
        await Module.DisposeAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Only this test's own root directory is removed; a wider delete would race with the
            // other tests xUnit runs in parallel.
            var root = Directory.GetParent(DataRoot)?.Parent;
            if (root is not null && root.FullName.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal))
            {
                Directory.Delete(root.FullName, recursive: true);
            }
        }
        catch (Exception)
        {
            // Test cleanup is best effort.
        }
    }
}

/// <summary>The shared default catalog shape the tests describe commands with.</summary>
internal static class TestCommands
{
    public const string SingleShellCommand = """
        commands:
          - id: decode_stack
            label: "Decode Kernel Stack"
            command: "/home/lixr/.local/bin/decode_kernel_stack"
            description: "Decodes the kernel stack."
            type: "shell"
          - id: replace_host
            label: "Replace Host Directory"
            command: "replace_host_directory"
            description: "Replaces local directory paths with remote URLs."
            type: "py"
          - id: fixed_host_command
            label: "Fixed Host Command"
            command: "/usr/bin/uptime"
            description: "Always runs on its own host."
            type: "shell"
            host: "r743"
        """;
}
