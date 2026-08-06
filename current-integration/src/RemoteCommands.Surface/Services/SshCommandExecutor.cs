using System.Diagnostics;
using System.Text;

namespace RemoteCommands.Surface.Services;

/// <summary>
/// Port of the original page3 SSHWorker: uploads input files to the remote host, executes the
/// shell command over SSH with a conda environment, streams output, and cleans up remote temp files.
/// </summary>
public sealed class SshCommandExecutor
{
    private const string MinicondaPath = "/home/lixr/miniconda3";

    public static string SshExecutable { get; } = ResolveOpenSshExecutable("ssh.exe");
    public static string ScpExecutable { get; } = ResolveOpenSshExecutable("scp.exe");

    public async Task<SshExecutionResult> RunAsync(
        string host,
        string command,
        string input1,
        string input2,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var temp1 = Path.Combine(Path.GetTempPath(), $"mpt-remote-commands-{Guid.NewGuid():N}.input1");
        var temp2 = Path.Combine(Path.GetTempPath(), $"mpt-remote-commands-{Guid.NewGuid():N}.input2");
        var file1 = Path.GetFileName(temp1);
        var file2 = Path.GetFileName(temp2);
        var output = new StringBuilder();

        void Emit(string line)
        {
            output.AppendLine(line);
            onOutput?.Invoke(line);
        }

        try
        {
            await File.WriteAllTextAsync(temp1, input1 ?? "", cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(temp2, input2 ?? "", cancellationToken).ConfigureAwait(false);

            Emit("Uploading input files...");
            await RunProcessAsync(
                ScpExecutable,
                [temp1, temp2, $"{host}:/tmp/"],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Emit("Executing remote command...");
            var remoteCommand =
                $"CONDA_EXE={MinicondaPath}/bin/conda {command} --file1 /tmp/{file1} --file2 /tmp/{file2}";
            var exitCode = await RunProcessAsync(
                SshExecutable,
                [host, remoteCommand],
                line => Emit(line),
                cancellationToken).ConfigureAwait(false);

            Emit(exitCode == 0 ? "Execution complete" : "Execution failed");
            return new SshExecutionResult(exitCode, output.ToString());
        }
        finally
        {
            TryDelete(temp1);
            TryDelete(temp2);
            try
            {
                await RunProcessAsync(
                    SshExecutable,
                    [host, $"rm -f /tmp/{file1} /tmp/{file2}"],
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Remote cleanup is best-effort.
            }
        }
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
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

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, onOutput, cancellationToken);
        var stderrTask = ReadLinesAsync(process.StandardError, onOutput, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            onOutput?.Invoke(line);
        }
    }

    private static string ResolveOpenSshExecutable(string fileName)
    {
        if (OperatingSystem.IsWindows())
        {
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
        }

        return fileName;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort local cleanup.
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
}

public sealed record SshExecutionResult(int ExitCode, string Output);
