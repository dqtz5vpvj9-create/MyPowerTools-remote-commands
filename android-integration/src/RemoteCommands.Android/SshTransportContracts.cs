namespace RemoteCommands.Android;

/// <summary>How a host authenticates. The catalog stores the kind; the value only lives in secret.store.</summary>
internal enum SshCredentialKind
{
    Password,
    PrivateKey
}

/// <summary>
/// Secret material for exactly one connection attempt. The value is read from secret.store at run
/// time, never serialized, never logged, and never echoed back in a command result.
/// </summary>
internal sealed record SshCredential(SshCredentialKind Kind, string Secret, string Passphrase = "")
{
    /// <summary>Redacted on purpose: an accidental ToString/interpolation must not leak key material.</summary>
    public override string ToString() => $"SshCredential({Kind}, <redacted>)";
}

/// <summary>Resolved connection parameters (no secret material).</summary>
internal sealed record SshConnectionRequest(
    string Alias,
    string Host,
    int Port,
    string Username,
    TimeSpan ConnectTimeout,
    TimeSpan CommandTimeout);

/// <summary>What the server presented for this connection, in the standard OpenSSH display form.</summary>
internal sealed record SshHostKeyObservation(string HostKeyName, string FingerprintSha256, int KeyLength);

/// <summary>
/// Raised when the server key is not the one the user confirmed. The caller turns this into the
/// explicit first-use / changed-key confirmation flow; it is never swallowed into a generic failure.
/// </summary>
internal sealed class SshHostKeyMismatchException : Exception
{
    public SshHostKeyMismatchException(
        SshHostKeyObservation observation,
        string? expectedFingerprint,
        string message)
        : base(message)
    {
        Observation = observation;
        ExpectedFingerprint = expectedFingerprint;
    }

    public SshHostKeyObservation Observation { get; }

    /// <summary>The fingerprint the user previously confirmed, or <see langword="null"/> on first use.</summary>
    public string? ExpectedFingerprint { get; }

    public bool IsFirstUse => string.IsNullOrEmpty(ExpectedFingerprint);
}

/// <summary>
/// The one seam between the module logic and the SSH client. Production uses
/// <see cref="SshNetTransport"/>; tests use a scripted in-memory transport, so no test ever opens a
/// socket and no test can pretend a real server was reached.
/// </summary>
internal interface ISshTransport
{
    bool IsAvailable { get; }

    /// <summary>Human readable reason the transport cannot run, empty when available.</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// Opens one connection. <paramref name="trustedFingerprint"/> is the fingerprint the user already
    /// confirmed, or <see langword="null"/> when this host was never confirmed. A server key that does
    /// not match must make this throw <see cref="SshHostKeyMismatchException"/>; the transport must
    /// never proceed on an unconfirmed key.
    /// </summary>
    Task<ISshSession> ConnectAsync(
        SshConnectionRequest request,
        SshCredential credential,
        string? trustedFingerprint,
        CancellationToken cancellationToken);
}

/// <summary>One connected SSH session: upload the two inputs, run the command, clean up, release.</summary>
internal interface ISshSession : IAsyncDisposable
{
    Task UploadAsync(string remotePath, string content, CancellationToken cancellationToken);

    /// <summary>Runs the remote command, forwarding output as it arrives. Returns the remote exit status.</summary>
    Task<int> ExecuteAsync(string commandLine, Action<string> onOutput, CancellationToken cancellationToken);

    /// <summary>Best-effort removal of the uploaded temporary files.</summary>
    Task DeleteFilesAsync(IReadOnlyList<string> remotePaths, CancellationToken cancellationToken);

    /// <summary>
    /// Kills the running remote command and drops the connection. Called from the cancel command and
    /// from module disable, so a cancelled or disabled run never keeps a live connection.
    /// </summary>
    void RequestCancel();
}
