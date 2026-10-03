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

    [AvaloniaFact]
    public async Task Local_transform_history_search_restore_clear_and_session_round_trip()
    {
        var vm = ViewModel();
        await vm.InitializeAsync();
        vm.SelectedCommandIndex = vm.Commands.ToList().FindIndex(command => command.Command == "add_extract_result_prefix");
        vm.Input1 = "alpha\nbeta";
        vm.Input2 = "secondary";
        vm.IsSecondInputVisible = true;
        vm.Host = "test-host";
        await vm.RunAsync();
        Assert.Equal("complete", vm.StatusKind);
        Assert.Contains("extract_result", vm.Output);
        var item = Assert.Single(vm.HistoryItems);
        vm.HistorySearch = "ALPHA";
        Assert.Single(vm.FilteredHistoryItems);
        vm.HistorySearch = "absent-token";
        Assert.Empty(vm.FilteredHistoryItems);
        vm.Input1 = "changed";
        vm.RestoreHistoryItem(item);
        Assert.Equal("alpha\nbeta", vm.Input1);
        Assert.Equal("secondary", vm.Input2);
        Assert.True(vm.IsSecondInputVisible);
        vm.ClearOutput();
        Assert.Equal("", vm.Output);
        Assert.Equal("idle", vm.StatusKind);
        await vm.SaveSessionStateAsync();
        var restored = ViewModel();
        await restored.InitializeAsync();
        Assert.Equal("test-host", restored.Host);
        Assert.Equal(vm.SelectedCommandIndex, restored.SelectedCommandIndex);
        Assert.True(restored.IsSecondInputVisible);
        await vm.ClearHistoryAsync();
        Assert.Empty(vm.HistoryItems);
        Assert.Empty(Store.LoadHistory());
    }

    [AvaloniaFact]
    public async Task Removed_command_cannot_be_rerun_and_unknown_transform_reports_failure()
    {
        var vm = ViewModel();
        await vm.InitializeAsync();
        vm.SelectedCommandIndex = vm.Commands.ToList().FindIndex(command => command.Command == "add_extract_result_prefix");
        vm.Input1 = "alpha";
        await vm.RunAsync();
        Store.SaveCommands("commands:\n  - id: unsupported\n    command: unknown_transform\n    type: py\n");
        await vm.ReloadCommandsAsync();
        await vm.RerunAsync();
        Assert.Equal("error", vm.StatusKind);
        Assert.Equal("该命令已不存在", vm.StatusText);
        await vm.RunAsync();
        Assert.Contains("no C# runtime mapping", vm.Output);
        Assert.Equal("error", vm.StatusKind);
        Assert.False(vm.IsRunning);
        Assert.Single(vm.HistoryItems);
    }

    [Fact]
    public void Invalid_catalog_preserves_previous_commands_and_settings_are_normalized()
    {
        Store.EnsureInitialized();
        var before = File.ReadAllText(Store.CommandsPath);
        Assert.Throws<InvalidDataException>(() => Store.SaveCommands("commands:\n  - id: bad\n    command: cmd\n    type: unsupported\n"));
        Assert.Equal(before, File.ReadAllText(Store.CommandsPath));
        Store.SaveSettings(new RemoteCommandsSettings(" test-host ", " code ", true, 0, "other-host", -4, "test-host\nTEST-HOST\nother-host\n-oUnsafe"));
        var settings = Store.LoadSettings();
        Assert.Equal("test-host", settings.DefaultHost);
        Assert.Equal(10, settings.HistoryRetention);
        Assert.Equal(0, settings.LastCommandIndex);
        Assert.Equal("test-host\nother-host", settings.KnownHosts);
    }

    [Fact]
    public async Task Corrupt_history_is_preserved_until_explicit_clear_and_retention_is_enforced()
    {
        Store.EnsureInitialized();
        var historyPath = Path.Combine(_root, "history.json");
        await File.WriteAllTextAsync(historyPath, "{corrupt");
        var item = new RemoteCommandHistoryItem("time", "label", "command", "py", "test-host", "one", "two", true, "output");
        await Store.AppendHistoryAsync(item, 10);
        Assert.Equal("{corrupt", await File.ReadAllTextAsync(historyPath));
        Store.ClearHistory();
        for (var i = 0; i < 12; i++) await Store.AppendHistoryAsync(item with { Output = i.ToString() }, 10);
        var history = Store.LoadHistory();
        Assert.Equal(10, history.Count);
        Assert.Equal("11", history[0].Output);
        Assert.Equal("2", history[^1].Output);
    }
}
