using RemoteCommands.Surface.Services;

namespace RemoteCommands.Android.Tests;

/// <summary>
/// Freezes the run contract the phone surface depends on: the two input files, the remote command
/// line, streamed output, cleanup, history and the payload shape.
/// </summary>
public sealed class RunFlowTests
{
    [Fact]
    public async Task RunUploadsBothInputsExecutesTheConfiguredCommandAndCleansUp()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        var result = await host.RunAsync("decode_stack", input1: "stack text", input2: "extra text");
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.True(result.Success);
        Assert.Equal(RemoteCommandRunStates.Succeeded, payload["state"]!.GetValue<string>());
        Assert.Equal(0, payload["exitCode"]!.GetValue<int>());
        Assert.Equal("managed-ssh", payload["transport"]!.GetValue<string>());
        Assert.Equal("r743", payload["alias"]!.GetValue<string>());
        Assert.Equal("192.168.22.24", payload["resolvedHost"]!.GetValue<string>());

        Assert.Equal(2, host.Transport.Session.Uploads.Count);
        var (path1, content1) = host.Transport.Session.Uploads[0];
        var (path2, content2) = host.Transport.Session.Uploads[1];
        Assert.Equal("stack text", content1);
        Assert.Equal("extra text", content2);
        Assert.StartsWith("/tmp/mpt-remote-commands-", path1, StringComparison.Ordinal);
        Assert.EndsWith(".input1", path1, StringComparison.Ordinal);
        Assert.EndsWith(".input2", path2, StringComparison.Ordinal);

        // Same remote command contract as the desktop executor: CONDA_EXE prefix plus --file1/--file2.
        Assert.Equal(
            $"CONDA_EXE=/home/lixr/miniconda3/bin/conda /home/lixr/.local/bin/decode_kernel_stack --file1 '{path1}' --file2 '{path2}'",
            host.Transport.Session.CommandLine);

        // Remote temp files are removed on the same session after a successful run.
        Assert.Equal([path1, path2], host.Transport.Session.DeletedPaths);
        Assert.True(host.Transport.Session.Disposed);

        var output = payload["output"]!.GetValue<string>();
        Assert.Contains("Uploading input files...", output, StringComparison.Ordinal);
        Assert.Contains("remote line 1", output, StringComparison.Ordinal);
        Assert.Contains("remote line 2", output, StringComparison.Ordinal);
        Assert.Contains("Execution complete", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroExitCodeKeepsTheOutputAndIsReported()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);
        host.Transport.Session.ExitCode = 7;

        var result = await host.RunAsync("decode_stack");
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.False(result.Success);
        Assert.Equal(RemoteCommandRunStates.Failed, payload["state"]!.GetValue<string>());
        Assert.Equal(7, payload["exitCode"]!.GetValue<int>());
        Assert.Contains("remote line 1", payload["output"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Execution failed", payload["output"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAliasWithoutARealHostFailsWithActionableGuidanceAndNeverConnects()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        var result = await host.RunAsync("decode_stack", host: "some-other-host");
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.False(result.Success);
        Assert.Equal(RemoteCommandRunStates.Failed, payload["state"]!.GetValue<string>());
        Assert.Contains("没有配置主机", payload["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("真实主机", payload["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, host.Transport.ConnectCount);
    }

    [Fact]
    public async Task PyCommandsRunLocallyWithoutSsh()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");

        var result = await host.RunAsync(
            "replace_host",
            input1: "see /home/lixr/aosp_host_working_dir/build.log");
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.True(result.Success);
        Assert.Equal("local", payload["transport"]!.GetValue<string>());
        Assert.Contains("http://r743.ipads-lab.se.sjtu.edu.cn:7112/build.log", payload["output"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, host.Transport.ConnectCount);
    }

    [Fact]
    public async Task ACommandWithAFixedHostIgnoresTheRequestedHost()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        var result = await host.RunAsync("fixed_host_command", host: "r743");
        Assert.True(result.Success);
        Assert.Equal("192.168.22.24", host.Transport.LastRequest!.Host);
        Assert.Equal(22, host.Transport.LastRequest.Port);
        Assert.Equal("lixr", host.Transport.LastRequest.Username);
    }

    [Fact]
    public async Task UnknownCommandIdFailsWithoutTouchingTheNetwork()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var result = await host.RunAsync("does-not-exist");
        Assert.False(result.Success);
        Assert.Contains("commands.yaml 中没有命令", result.Error?.Message ?? "", StringComparison.Ordinal);
        Assert.Equal(0, host.Transport.ConnectCount);
    }

    [Fact]
    public async Task HistoryIsAppendedWithTheDesktopShape()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        await host.RunAsync("decode_stack", input1: "in-one", input2: "in-two");

        var history = host.Store.LoadHistory();
        var item = Assert.Single(history);
        Assert.Equal("Decode Kernel Stack", item.Label);
        Assert.Equal("/home/lixr/.local/bin/decode_kernel_stack", item.Command);
        Assert.Equal("shell", item.Type);
        Assert.Equal("r743", item.Host);
        Assert.Equal("in-one", item.Input1);
        Assert.Equal("in-two", item.Input2);
        Assert.True(item.SecondInputEnabled);
        Assert.Contains("remote line 1", item.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputIsStreamedAsBatchedModuleEvents()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        var events = new List<MyPowerTools.Abstractions.MptModuleEvent>();
        var pump = Task.Run(async () =>
        {
            await foreach (var item in host.Module.SubscribeEventsAsync(
                               new MyPowerTools.Abstractions.EventCursor(0),
                               CancellationToken.None))
            {
                lock (events)
                {
                    events.Add(item);
                }
            }
        });

        await host.RunAsync("decode_stack");
        await host.Module.DisposeAsync(CancellationToken.None);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        var outputEvents = events.Where(item => item.Type == "command.output").ToArray();
        Assert.NotEmpty(outputEvents);
        var lines = outputEvents
            .SelectMany(item => (item.Payload["lines"] as JsonArray)?.Select(node => node!.GetValue<string>()) ?? [])
            .ToArray();
        Assert.Contains("remote line 1", lines);
        Assert.Contains("remote line 2", lines);

        Assert.Contains(events, item => item.Type == "run.started");
        var finished = Assert.Single(events.Where(item => item.Type == "run.finished"));
        Assert.Equal(RemoteCommandRunStates.Succeeded, finished.Payload["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheConfiguredCondaExecutableIsUsed()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        var applied = await host.Module.ApplySettingsAsync(
            new MyPowerTools.Abstractions.SettingsSnapshotDocument(
                RemoteCommandsAndroidOptions.ModuleId,
                1,
                new JsonObject { ["condaExecutable"] = "/opt/conda/bin/conda" },
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal("/opt/conda/bin/conda", applied.Values["condaExecutable"]!.GetValue<string>());

        await host.RunAsync("decode_stack");
        Assert.StartsWith(
            "CONDA_EXE=/opt/conda/bin/conda ",
            host.Transport.Session.CommandLine!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsRejectSecretKeysAndUnknownKeys()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        var validation = await host.Module.ValidateSettingsAsync(
            new MyPowerTools.Abstractions.SettingsPatch(
                RemoteCommandsAndroidOptions.ModuleId,
                1,
                new JsonObject { ["password"] = "hunter2" }),
            CancellationToken.None);
        Assert.False(validation.Ok);

        // Read-only values the module itself reports may round-trip through an apply; they are
        // recomputed, never echoed back from the caller's payload.
        var applied = await host.Module.ApplySettingsAsync(
            new MyPowerTools.Abstractions.SettingsSnapshotDocument(
                RemoteCommandsAndroidOptions.ModuleId,
                1,
                new JsonObject { ["hostCount"] = 3, ["activeRun"] = "not-a-real-state" },
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(0, applied.Values["hostCount"]!.GetValue<int>());
        Assert.Equal("idle", applied.Values["activeRun"]!.GetValue<string>());
    }

    [Fact]
    public async Task CatalogReportsTheSharedCommandsYaml()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-catalog", RemoteCommandsAndroidOptions.CommandCatalog, new JsonObject()),
            CancellationToken.None);
        var payload = RemoteCommandsTestHost.Parse(result);
        var commands = payload["commands"]!.AsArray();

        Assert.Equal(3, commands.Count);
        Assert.Equal(
            RemoteCommandsAndroidPaths.CommandsPath(host.DataRoot),
            payload["commandsPath"]!.GetValue<string>());
        Assert.Equal("", payload["error"]!.GetValue<string>());
        Assert.Contains(commands, item => item!["id"]!.GetValue<string>() == "decode_stack");
        Assert.Contains(commands, item => item!["type"]!.GetValue<string>() == "py" && !item["usesRemoteHost"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HostListFlagsAliasesThatHaveNoRealHostYet()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-hosts", RemoteCommandsAndroidOptions.CommandHostsList, new JsonObject()),
            CancellationToken.None);
        var missing = RemoteCommandsTestHost.Parse(result)["missingAliases"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();

        Assert.Contains("r743", missing);

        await host.AddHostAsync();
        var after = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-hosts", RemoteCommandsAndroidOptions.CommandHostsList, new JsonObject()),
            CancellationToken.None);
        Assert.Empty(RemoteCommandsTestHost.Parse(after)["missingAliases"]!.AsArray());
    }

    [Fact]
    public async Task HistoryClearRemovesTheSharedHistoryFile()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);
        await host.RunAsync("decode_stack");
        Assert.Single(host.Store.LoadHistory());

        var cleared = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-clear", RemoteCommandsAndroidOptions.CommandHistoryClear, new JsonObject()),
            CancellationToken.None);
        Assert.True(cleared.Success);
        Assert.Empty(host.Store.LoadHistory());
    }

    [Fact]
    public async Task UploadFailureStillReleasesTheSessionAndTheLease()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);
        host.Transport.Session.UploadFailure = new IOException("no space left on device");

        var result = await host.RunAsync("decode_stack");
        Assert.False(result.Success);
        Assert.True(host.Transport.Session.Disposed);
        Assert.Equal(0, host.Background.ActiveCount);
        Assert.Equal(0, host.Transport.Session.ExecuteCount);

        // A later run is possible again.
        host.Transport.Session.UploadFailure = null;
        Assert.True((await host.RunAsync("decode_stack", invocationId: "invoke-run-2")).Success);
    }
}
