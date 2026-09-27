using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteCommands.Android;

/// <summary>
/// Production SSH transport: one fully managed SSH.NET client per run, no ssh.exe / scp.exe and no
/// platform <c>network.ssh</c> capability.
///
/// Security behaviour that must not regress:
/// <list type="bullet">
///   <item>the server key is verified through <see cref="BaseClient.HostKeyReceived"/> against the
///   fingerprint the user explicitly confirmed. <c>CanTrust</c> starts false and is only set for an
///   exact match, so there is no "accept anything" default and an unknown or changed key aborts the
///   connection before authentication;</item>
///   <item>the credential is passed straight to the authentication method and is never written to a
///   file, log or result;</item>
///   <item>the SFTP upload reuses the verified <see cref="ConnectionInfo"/> and the same key check, so
///   the second connection cannot be hijacked after the first one was verified;</item>
///   <item>cancel kills the remote command, drops the connection and disposes the client, so a
///   cancelled run leaves no live session behind.</item>
/// </list>
/// </summary>
internal sealed class SshNetTransport : ISshTransport
{
    /// <summary>True when the SSH.NET assembly actually loaded into this process.</summary>
    public static bool IsSupported { get; } = ProbeAssembly();

    public bool IsAvailable => true;

    public string UnavailableReason => "";

    public static SshNetTransport? TryCreate() => new();

    private static bool ProbeAssembly()
    {
        try
        {
            _ = typeof(SshClient).Assembly;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<ISshSession> ConnectAsync(
        SshConnectionRequest request,
        SshCredential credential,
        string? trustedFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        var verifier = new HostKeyVerifier(trustedFingerprint);
        var connectionInfo = CreateConnectionInfo(request, credential);
        var client = new SshClient(connectionInfo);
        client.HostKeyReceived += verifier.OnHostKeyReceived;
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            client.Dispose();
            throw Translate(exception, verifier, request);
        }

        return new SshNetSession(client, verifier, request);
    }

    private static ConnectionInfo CreateConnectionInfo(SshConnectionRequest request, SshCredential credential)
    {
        AuthenticationMethod authentication = credential.Kind switch
        {
            SshCredentialKind.Password => new PasswordAuthenticationMethod(request.Username, credential.Secret),
            SshCredentialKind.PrivateKey => new PrivateKeyAuthenticationMethod(
                request.Username,
                CreatePrivateKey(credential)),
            _ => throw new InvalidOperationException($"Unsupported SSH credential kind '{credential.Kind}'.")
        };

        return new ConnectionInfo(request.Host, request.Port, request.Username, authentication)
        {
            Timeout = request.ConnectTimeout
        };
    }

    private static PrivateKeyFile CreatePrivateKey(SshCredential credential)
    {
        var material = new MemoryStream(Encoding.UTF8.GetBytes(credential.Secret), writable: false);
        try
        {
            return string.IsNullOrEmpty(credential.Passphrase)
                ? new PrivateKeyFile(material)
                : new PrivateKeyFile(material, credential.Passphrase);
        }
        catch
        {
            material.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Turns SSH.NET failures into errors the phone can act on, without ever including key material.
    /// A rejected host key is reported as the dedicated exception so the module can offer the
    /// explicit confirmation flow instead of a generic "connection failed".
    /// </summary>
    private static Exception Translate(Exception exception, HostKeyVerifier verifier, SshConnectionRequest request)
    {
        if (verifier.RejectedObservation is { } rejected)
        {
            return new SshHostKeyMismatchException(
                rejected,
                verifier.TrustedFingerprint,
                verifier.TrustedFingerprint is null
                    ? $"{request.Host}:{request.Port} 的主机密钥尚未确认（{rejected.HostKeyName} {rejected.FingerprintSha256}）。"
                    : $"{request.Host}:{request.Port} 的主机密钥已变化（当前 {rejected.HostKeyName} {rejected.FingerprintSha256}）。");
        }

        return exception switch
        {
            SshAuthenticationException => new InvalidOperationException(
                $"{request.Host} 拒绝了 SSH 身份验证，请检查用户名与凭据：{exception.Message}", exception),
            SshConnectionException => new InvalidOperationException(
                $"无法连接 {request.Host}:{request.Port}：{exception.Message}", exception),
            System.Net.Sockets.SocketException => new InvalidOperationException(
                $"无法连接 {request.Host}:{request.Port}：{exception.Message}", exception),
            OperationCanceledException => exception,
            _ => exception
        };
    }

    /// <summary>
    /// Host key policy. The fingerprint comes from SSH.NET's own SHA256 host key fingerprint; the
    /// module performs no extra hashing and stores exactly the value the user confirmed.
    /// </summary>
    private sealed class HostKeyVerifier(string? trustedFingerprint)
    {
        public string? TrustedFingerprint { get; } = trustedFingerprint;

        public SshHostKeyObservation? RejectedObservation { get; private set; }

        public void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
        {
            var observation = new SshHostKeyObservation(e.HostKeyName, e.FingerPrintSHA256, e.KeyLength);
            var matches = TrustedFingerprint is { Length: > 0 } trusted &&
                          string.Equals(trusted, observation.FingerprintSha256, StringComparison.Ordinal);
            if (matches)
            {
                e.CanTrust = true;
                return;
            }

            // No default trust: an unconfirmed or rotated key stops the handshake here.
            e.CanTrust = false;
            RejectedObservation = observation;
        }
    }

    private sealed class SshNetSession : ISshSession
    {
        private readonly SshClient _client;
        private readonly HostKeyVerifier _verifier;
        private readonly SshConnectionRequest _request;
        private SshCommand? _activeCommand;
        private int _cancelled;
        private bool _disposed;

        public SshNetSession(SshClient client, HostKeyVerifier verifier, SshConnectionRequest request)
        {
            _client = client;
            _verifier = verifier;
            _request = request;
        }

        public async Task UploadAsync(string remotePath, string content, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            using var sftp = new SftpClient(_client.ConnectionInfo);
            sftp.HostKeyReceived += _verifier.OnHostKeyReceived;
            await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var payload = new MemoryStream(Encoding.UTF8.GetBytes(content ?? ""), writable: false);
                await using (payload.ConfigureAwait(false))
                {
                    // UploadFileAsync uses CreateNewOrOpen, i.e. the desktop scp overwrite behaviour.
                    await sftp.UploadFileAsync(payload, remotePath, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    sftp.Disconnect();
                }
                catch (Exception)
                {
                    // Best effort: the upload result is what matters.
                }
            }
        }

        public async Task<int> ExecuteAsync(
            string commandLine,
            Action<string> onOutput,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            using var command = _client.CreateCommand(commandLine);
            command.CommandTimeout = _request.CommandTimeout;
            _activeCommand = command;

            // Reading the two pipe streams is the documented way to observe output while the command
            // is still running: SSH.NET writes into them as channel data arrives and ends the reads by
            // disposing the streams when the command is disposed.
            var stdout = PumpAsync(command.OutputStream, onOutput);
            var stderr = PumpAsync(command.ExtendedOutputStream, onOutput);
            try
            {
                await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                return command.ExitStatus ?? -1;
            }
            finally
            {
                _activeCommand = null;
                command.Dispose();
                await AwaitPumpsAsync(stdout, stderr).ConfigureAwait(false);
            }
        }

        public Task DeleteFilesAsync(IReadOnlyList<string> remotePaths, CancellationToken cancellationToken)
        {
            if (_disposed || _cancelled != 0 || remotePaths.Count == 0)
            {
                return Task.CompletedTask;
            }

            // Cleanup is best effort, exactly like the desktop executor's trailing rm -f: a failed
            // cleanup never turns a finished run into an error.
            return Task.Run(
                () =>
                {
                    try
                    {
                        if (!_client.IsConnected)
                        {
                            return;
                        }

                        var quoted = string.Join(' ', remotePaths.Select(path => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
                        using var cleanup = _client.CreateCommand($"rm -f {quoted}");
                        cleanup.CommandTimeout = TimeSpan.FromSeconds(15);
                        cleanup.Execute();
                    }
                    catch (Exception)
                    {
                        // Remote cleanup is best effort.
                    }
                },
                cancellationToken);
        }

        public void RequestCancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0)
            {
                return;
            }

            try
            {
                _activeCommand?.CancelAsync(forceKill: true, millisecondsTimeout: 500);
            }
            catch (Exception)
            {
                // The command may have completed between the check and the signal.
            }

            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch (Exception)
            {
                // Dropping the connection is best effort; Dispose still releases it.
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            try
            {
                _client.Dispose();
            }
            catch (Exception)
            {
                // Best effort release.
            }

            return ValueTask.CompletedTask;
        }

        private static async Task PumpAsync(Stream stream, Action<string>? onOutput)
        {
            if (onOutput is null)
            {
                return;
            }

            var buffer = new byte[4096];
            var pending = new StringBuilder();
            try
            {
                while (true)
                {
                    var read = await Task.Run(() => stream.Read(buffer, 0, buffer.Length)).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    while (true)
                    {
                        var text = pending.ToString();
                        var newline = text.IndexOf('\n', StringComparison.Ordinal);
                        if (newline < 0)
                        {
                            break;
                        }

                        var line = text[..newline].TrimEnd('\r');
                        pending.Remove(0, newline + 1);
                        onOutput(line);
                    }
                }

                if (pending.Length > 0)
                {
                    onOutput(pending.ToString().TrimEnd('\r'));
                }
            }
            catch (Exception)
            {
                // The stream is disposed when the command completes or is cancelled; buffered output
                // was already forwarded line by line.
            }
        }

        private static async Task AwaitPumpsAsync(params Task[] pumps)
        {
            try
            {
                await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Output pumping is inherently best effort once the command has completed.
            }
        }
    }
}
