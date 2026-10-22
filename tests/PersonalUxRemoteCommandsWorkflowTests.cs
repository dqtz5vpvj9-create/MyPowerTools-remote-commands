using Avalonia.Headless.XUnit;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;
using RemoteCommands.Surface.ViewModels;
using Xunit;

namespace PersonalUx.Tests;

public sealed class PersonalUxRemoteCommandsWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-remote-workflow-" + Guid.NewGuid().ToString("N"));
    private RemoteCommandsStore Store => new(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private RemoteCommandsViewModel ViewModel(SshCommandExecutor? executor = null) => new(
        new MptAvaloniaSurfaceContext("remote-commands", "workspace", _root, "light",
            (_, _, _) => throw new NotSupportedException(), (_, _, _) => Task.CompletedTask, null!, _ => { }), executor);

    [Fact]
    public async Task Upload_protocol_transmits_both_inputs_streams_output_and_cleans_files()
    {
        var calls = new List<string[]>();
        var executor = new SshCommandExecutor(async (_, arguments, emit, _) =>
        {
            calls.Add(arguments.ToArray());
            if (arguments.Count == 3)
            {
                Assert.Equal("first\n中文", await File.ReadAllTextAsync(arguments[0]));
                Assert.Equal("second", await File.ReadAllTextAsync(arguments[1]));
                Assert.Equal("test-host:/tmp/", arguments[2]);
            }
            else if (!arguments[1].StartsWith("rm -f")) emit?.Invoke("command output");
            return 0;
        });
        var result = await executor.RunAsync("test-host", "safe-command", "first\n中文", "second");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("command output", result.Output);
        Assert.Contains("safe-command --file1 /tmp/", calls[1][1]);
        Assert.Contains(" --file2 /tmp/", calls[1][1]);
        Assert.StartsWith("rm -f /tmp/", calls[2][1]);
        Assert.All(calls[0].Take(2), path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task Upload_failure_stops_command_and_still_cleans_files()
    {
        var calls = new List<string[]>();
        var executor = new SshCommandExecutor((_, arguments, _, _) =>
        { calls.Add(arguments.ToArray()); return Task.FromResult(arguments.Count == 3 ? 9 : 0); });
        var error = await Assert.ThrowsAsync<IOException>(() => executor.RunAsync("test-host", "safe-command", "a", "b"));
        Assert.Contains("exit code 9", error.Message);
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("rm -f", calls[1][1]);
        Assert.All(calls[0].Take(2), path => Assert.False(File.Exists(path)));
    }

    [Theory]
    [InlineData("-oProxyCommand=unsafe")]
    [InlineData("host with space")]
    [InlineData("")]
    public async Task Invalid_host_never_starts_upload_or_cleanup(string host)
    {
        var calls = 0;
        var executor = new SshCommandExecutor((_, _, _, _) => { calls++; return Task.FromResult(0); });
        await Assert.ThrowsAsync<ArgumentException>(() => executor.RunAsync(host, "safe-command", "", ""));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cancellation_cleans_temp_inputs_and_uses_uncancelled_cleanup_token()
    {
        using var cancellation = new CancellationTokenSource();
        string[]? paths = null;
        var cleaned = false;
        var executor = new SshCommandExecutor((_, arguments, _, token) =>
        {
            if (arguments.Count == 3) { paths = arguments.Take(2).ToArray(); cancellation.Cancel(); }
            else { Assert.False(token.IsCancellationRequested); cleaned = true; }
            return Task.FromResult(0);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.RunAsync("test-host", "safe-command", "a", "b", cancellationToken: cancellation.Token));
        Assert.True(cleaned);
        Assert.All(paths!, path => Assert.False(File.Exists(path)));
    }

    [AvaloniaFact]
    public async Task Remote_failure_releases_busy_state_and_does_not_record_success_history()
    {
        var vm = ViewModel(new SshCommandExecutor((_, arguments, _, _) => Task.FromResult(
            arguments.Count == 3 || arguments[1].StartsWith("rm -f") ? 0 : 17)));
        await vm.InitializeAsync();
        await vm.RunAsync();
        Assert.Equal("error", vm.StatusKind);
        Assert.Contains("exit code 17", vm.Output);
        Assert.False(vm.IsRunning);
        Assert.Empty(vm.HistoryItems);
    }

}
