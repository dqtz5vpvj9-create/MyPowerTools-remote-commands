using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteCommands.Surface.Services;

public sealed record RemoteCommandExecutionResult(int ExitCode, string Output);

/// <summary>
/// Executes normalized catalog entries. SSH and local-process commands share the same
/// placeholder model, timeout behavior, streaming output and temporary-file lifecycle.
/// </summary>
public sealed class RemoteCommandExecutionService
{
    public static string SshExecutable { get; } = ResolveOpenSshExecutable("ssh");
    public static string ScpExecutable { get; } = ResolveOpenSshExecutable("scp");

    public async Task<RemoteCommandExecutionResult> RunAsync(
        RemoteCommandDefinition command,
        string host,
        IReadOnlyDictionary<string, string> inputs,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));

        try
        {
            return command.Runner switch
            {
                RemoteCommandRunners.Transform => await Task.Run(
                    () => RunTransform(command, inputs),
                    timeout.Token).ConfigureAwait(false),
                RemoteCommandRunners.Local => await RunLocalAsync(command, inputs, onOutput, timeout.Token)
                    .ConfigureAwait(false),
                RemoteCommandRunners.Ssh => await RunSshAsync(command, host, inputs, onOutput, timeout.Token)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported command runner '{command.Runner}'.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Command '{command.Label}' exceeded its {command.TimeoutSeconds}-second timeout.");
        }
    }

    public static bool IsValidSshDestination(string value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "An SSH host is required.";
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('-', StringComparison.Ordinal))
        {
            error = "SSH destinations cannot begin with '-'.";
            return false;
        }

        if (!Regex.IsMatch(
                trimmed,
                @"^(?:[A-Za-z0-9._%+\-]+@)?(?:[A-Za-z0-9._\-]+|\[[0-9A-Fa-f:]+\])$",
                RegexOptions.CultureInvariant))
        {
            error = "Use an SSH config alias, host name, user@host, or a bracketed IPv6 address.";
            return false;
        }

        error = null;
        return true;
    }

    private static RemoteCommandExecutionResult RunTransform(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> inputs)
    {
        var firstInputId = command.Inputs.FirstOrDefault()?.Id ?? "input1";
        var source = inputs.GetValueOrDefault(firstInputId, "");
        var output = RemoteCommandsTextTransforms.Apply(command.Command, source);
        return new RemoteCommandExecutionResult(0, output);
    }

    private static async Task<RemoteCommandExecutionResult> RunSshAsync(
        RemoteCommandDefinition command,
        string host,
        IReadOnlyDictionary<string, string> inputs,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        if (!IsValidSshDestination(host, out var hostError))
        {
            throw new InvalidOperationException(hostError);
        }

        var transcript = new ExecutionTranscript(onOutput);
        var runId = $"mpt-remote-commands-{Guid.NewGuid():N}";
        var localDirectory = Path.Combine(Path.GetTempPath(), runId);
        var remoteDirectory = $"/tmp/{runId}";
        var remoteFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remoteDirectoryCreated = false;

        try
        {
            var localFiles = await WriteInputFilesAsync(command, inputs, localDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (localFiles.Count > 0)
            {
                transcript.Emit("Preparing remote input directory...");
                var mkdirExit = await RunProcessAsync(
                    CreateSshStartInfo(host, $"mkdir -m 700 -- {RemoteCommandTemplate.ShellQuote(remoteDirectory)}"),
                    transcript.Emit,
                    cancellationToken).ConfigureAwait(false);
                if (mkdirExit != 0)
                {
                    throw new InvalidOperationException($"SSH mkdir failed with exit code {mkdirExit}.");
                }

                remoteDirectoryCreated = true;
                transcript.Emit($"Uploading {localFiles.Count} input file(s)...");
                var scpArguments = localFiles.Values
                    .Append($"{host}:{remoteDirectory}/")
                    .ToArray();
                var scpExit = await RunProcessAsync(
                    CreateScpStartInfo(scpArguments),
                    transcript.Emit,
                    cancellationToken).ConfigureAwait(false);
                if (scpExit != 0)
                {
                    throw new InvalidOperationException($"SCP upload failed with exit code {scpExit}.");
                }

                foreach (var pair in localFiles)
                {
                    remoteFiles[pair.Key] = $"{remoteDirectory}/{Path.GetFileName(pair.Value)}";
                }
            }

            var remoteCommand = RemoteCommandTemplate.BuildShellCommand(command, inputs, remoteFiles);
            if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            {
                remoteCommand =
                    $"cd -- {RemoteCommandTemplate.ShellQuote(command.WorkingDirectory)} && {remoteCommand}";
            }

            transcript.Emit("Executing remote command...");
            var exitCode = await RunProcessAsync(
                CreateSshStartInfo(host, remoteCommand),
                transcript.Emit,
                cancellationToken).ConfigureAwait(false);
            transcript.Emit(exitCode == 0
                ? "Remote command completed."
                : $"Remote command failed with exit code {exitCode}.");
            return new RemoteCommandExecutionResult(exitCode, transcript.Text);
        }
        finally
        {
            TryDeleteDirectory(localDirectory);
            if (remoteDirectoryCreated)
            {
                await TryCleanupRemoteDirectoryAsync(host, remoteDirectory).ConfigureAwait(false);
            }
        }
    }

    private static async Task<RemoteCommandExecutionResult> RunLocalAsync(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> inputs,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        var transcript = new ExecutionTranscript(onOutput);
        var localDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mpt-remote-commands-{Guid.NewGuid():N}");

        try
        {
            var localFiles = await WriteInputFilesAsync(command, inputs, localDirectory, cancellationToken)
                .ConfigureAwait(false);
            var startInfo = CreateLocalStartInfo(command, inputs, localFiles);
            transcript.Emit("Executing local command...");
            var exitCode = await RunProcessAsync(startInfo, transcript.Emit, cancellationToken)
                .ConfigureAwait(false);
            transcript.Emit(exitCode == 0
                ? "Local command completed."
                : $"Local command failed with exit code {exitCode}.");
            return new RemoteCommandExecutionResult(exitCode, transcript.Text);
        }
        finally
        {
            TryDeleteDirectory(localDirectory);
        }
    }

    private static ProcessStartInfo CreateLocalStartInfo(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> files)
    {
        ProcessStartInfo startInfo;
        if (command.Arguments.Count > 0)
        {
            startInfo = CreateProcessStartInfo(
                RemoteCommandTemplate.RenderLocalExecutable(command, inputs, files),
                RemoteCommandTemplate.RenderLocalArguments(command, inputs, files));
        }
        else if (OperatingSystem.IsWindows())
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo = CreateProcessStartInfo(
                shell,
                ["/d", "/s", "/c", RemoteCommandTemplate.RenderLocalShellCommand(command, inputs, files)]);
        }
        else
        {
            startInfo = CreateProcessStartInfo(
                "/bin/sh",
                ["-lc", RemoteCommandTemplate.RenderLocalShellCommand(command, inputs, files)]);
        }

        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            startInfo.WorkingDirectory = command.WorkingDirectory;
        }

        foreach (var pair in command.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private static async Task<Dictionary<string, string>> WriteInputFilesAsync(
        RemoteCommandDefinition command,
        IReadOnlyDictionary<string, string> inputs,
        string directory,
        CancellationToken cancellationToken)
    {
        var fileInputs = RemoteCommandTemplate.GetFileInputIds(command);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fileInputs.Count == 0)
        {
            return files;
        }

        Directory.CreateDirectory(directory);
        foreach (var id in fileInputs)
        {
            var path = Path.Combine(directory, id + ".txt");
            await File.WriteAllTextAsync(
                path,
                inputs.GetValueOrDefault(id, ""),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            files[id] = path;
        }

        return files;
    }

    private static ProcessStartInfo CreateSshStartInfo(string host, string remoteCommand) =>
        CreateProcessStartInfo(
            SshExecutable,
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", host, remoteCommand]);

    private static ProcessStartInfo CreateScpStartInfo(IEnumerable<string> transferArguments) =>
        CreateProcessStartInfo(
            ScpExecutable,
            new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=15" }
                .Concat(transferArguments));

    private static ProcessStartInfo CreateProcessStartInfo(
        string fileName,
        IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Could not start '{startInfo.FileName}': {ex.Message}",
                ex);
        }

        var stdout = ReadLinesAsync(process.StandardOutput, onOutput, cancellationToken);
        var stderr = ReadLinesAsync(process.StandardError, onOutput, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Stream readers can observe cancellation or disposal after process termination.
            }

            throw;
        }
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            onOutput(line);
        }
    }

    private static async Task TryCleanupRemoteDirectoryAsync(string host, string directory)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RunProcessAsync(
                CreateSshStartInfo(
                    host,
                    $"rm -rf -- {RemoteCommandTemplate.ShellQuote(directory)}"),
                _ => { },
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Remote cleanup is best-effort and bounded so closing the Surface cannot hang.
        }
    }

    private static string ResolveOpenSshExecutable(string baseName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return baseName;
        }

        var fileName = baseName + ".exe";
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH-Win64", fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", fileName)
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return fileName;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Local cleanup is best-effort.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cancellation.
        }
    }

    private sealed class ExecutionTranscript
    {
        private readonly object _gate = new();
        private readonly StringBuilder _builder = new();
        private readonly Action<string>? _onOutput;

        public ExecutionTranscript(Action<string>? onOutput)
        {
            _onOutput = onOutput;
        }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _builder.ToString();
                }
            }
        }

        public void Emit(string line)
        {
            lock (_gate)
            {
                _builder.AppendLine(line);
            }

            _onOutput?.Invoke(line);
        }
    }
}
