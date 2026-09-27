namespace RemoteCommands.Android.Tests;

/// <summary>
/// Freezes the cancel/disable contract: an explicit cancel or a module disable ends the running
/// command, drops the session, releases the foreground-activity lease, and never leaves the module
/// believing a run is still active.
/// </summary>
public sealed class CancellationTests
{
    private static async Task<(RemoteCommandsTestHost Host, Task<MyPowerTools.Abstractions.CommandExecutionResult> Run, TaskCompletionSource Started)> StartBlockingRunAsync()
    {
        var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Transport.Session.ExecuteOverride = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        };

        var run = host.RunAsync("decode_stack");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (host, run, started);
    }

    [Fact]
    public async Task CancelEndsTheRunDropsTheSessionAndReleasesTheLease()
    {
        var (host, run, _) = await StartBlockingRunAsync();
        await using var _host = host;

        Assert.Equal(1, host.Background.BeginCount);
        Assert.Equal(1, host.Background.ActiveCount);

        var cancelled = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-cancel", RemoteCommandsAndroidOptions.CommandCancel, new JsonObject()),
            CancellationToken.None);
        Assert.True(RemoteCommandsTestHost.Parse(cancelled)["cancelled"]!.GetValue<bool>());

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.Equal(RemoteCommandRunStates.Cancelled, payload["state"]!.GetValue<string>());
        Assert.False(result.Success);
        Assert.True(host.Transport.Session.CancelRequested);
        Assert.True(host.Transport.Session.Disposed);

        // A cancelled run cannot reach the server again, so remote cleanup is skipped instead of
        // pretending it happened.
        Assert.Empty(host.Transport.Session.DeletedPaths);

        Assert.Equal(1, host.Background.DisposeCount);
        Assert.Equal(0, host.Background.ActiveCount);

        var status = RemoteCommandsTestHost.Parse(await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-status", RemoteCommandsAndroidOptions.CommandStatus, new JsonObject()),
            CancellationToken.None));
        Assert.Equal("idle", status["activeStage"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisablingTheModuleCancelsTheRun()
    {
        var (host, run, _) = await StartBlockingRunAsync();
        await using var _host = host;

        await host.Module.DisableAsync(
            new MyPowerTools.Abstractions.ModuleContext(
                "0.4.0", "1.0", RemoteCommandsAndroidOptions.PackageId, RemoteCommandsAndroidOptions.ModuleId,
                host.DataRoot, host.DataRoot, host.DataRoot, "android-arm64", [], null),
            CancellationToken.None);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            RemoteCommandRunStates.Cancelled,
            RemoteCommandsTestHost.Parse(result)["state"]!.GetValue<string>());
        Assert.True(host.Transport.Session.CancelRequested);
        Assert.Equal(0, host.Background.ActiveCount);
    }

    [Fact]
    public async Task CancellingWithoutAnActiveRunReportsFalse()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-cancel", RemoteCommandsAndroidOptions.CommandCancel, new JsonObject()),
            CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(RemoteCommandsTestHost.Parse(result)["cancelled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ASecondRunIsRejectedWhileOneIsActive()
    {
        var (host, run, _) = await StartBlockingRunAsync();
        await using var _host = host;

        var second = await host.RunAsync("decode_stack", invocationId: "invoke-run-2");
        Assert.False(second.Success);
        Assert.Equal(1, host.Transport.Session.ExecuteCount);
        Assert.Contains("已有远程命令正在执行", second.Error?.Message ?? "", StringComparison.Ordinal);

        await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-cancel", RemoteCommandsAndroidOptions.CommandCancel, new JsonObject()),
            CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellationTokenFromTheCallerAlsoStopsTheRun()
    {
        var (host, run, _) = await StartBlockingRunAsync();
        await using var _host = host;

        // The host cancels the invocation (command palette stop); the same teardown must happen.
        await host.Module.DisposeAsync(CancellationToken.None);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            RemoteCommandRunStates.Cancelled,
            RemoteCommandsTestHost.Parse(result)["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task MissingBackgroundCapabilityStillRunsButHoldsNoLease()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(
            TestCommands.SingleShellCommand,
            withBackground: false);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        var result = await host.RunAsync("decode_stack");
        Assert.True(result.Success);
        Assert.Equal(0, host.Background.BeginCount);
    }

    [Fact]
    public async Task ANotificationPermissionFailureDoesNotLoseTheRun()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);
        host.Background.FailWith = new UnauthorizedAccessException("请允许通知");

        var result = await host.RunAsync("decode_stack");
        Assert.True(result.Success);
        Assert.Equal(0, host.Background.ActiveCount);
    }
}
