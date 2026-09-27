namespace RemoteCommands.Android.Tests;

/// <summary>
/// Freezes the catalog.save contract the phone page uses to paste/edit commands.yaml:
/// only the module's canonical file is ever written, an invalid document changes nothing, and a valid
/// document is immediately visible to the catalog and to later runs.
/// </summary>
public sealed class CatalogSaveTests
{
    private const string Replacement = """
        commands:
          - id: phone_added_command
            label: "Phone Added Command"
            command: "/usr/bin/hostname"
            description: "Added from the phone."
            type: "shell"
        """;

    private static string CommandsPath(RemoteCommandsTestHost host) =>
        RemoteCommandsAndroidPaths.CommandsPath(host.DataRoot);

    private static Task<MyPowerTools.Abstractions.CommandExecutionResult> SaveAsync(
        RemoteCommandsTestHost host,
        string content,
        JsonObject? extraArgs = null) =>
        host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-save",
                RemoteCommandsAndroidOptions.CommandCatalogSave,
                BuildArgs(content, extraArgs)),
            CancellationToken.None).AsTask();

    private static JsonObject BuildArgs(string content, JsonObject? extraArgs)
    {
        var args = new JsonObject { [RemoteCommandsAndroidOptions.ArgumentContent] = content };
        foreach (var pair in extraArgs ?? new JsonObject())
        {
            args[pair.Key] = pair.Value?.DeepClone();
        }

        return args;
    }

    [Fact]
    public async Task AValidDocumentIsWrittenToTheCanonicalFileAndBecomesVisible()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        var result = await SaveAsync(host, Replacement);
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.True(result.Success);
        Assert.True(payload["saved"]!.GetValue<bool>());
        Assert.Equal(CommandsPath(host), payload["commandsPath"]!.GetValue<string>());
        Assert.Equal(Replacement.Length, payload["contentLength"]!.GetValue<int>());

        // The shared file itself now holds exactly what was submitted.
        Assert.Equal(Replacement, await File.ReadAllTextAsync(CommandsPath(host)));

        // The catalog command reports the new document, not the module's previous in-memory copy.
        var catalog = RemoteCommandsTestHost.Parse(await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-catalog", RemoteCommandsAndroidOptions.CommandCatalog, new JsonObject()),
            CancellationToken.None));
        var commands = catalog["commands"]!.AsArray();
        var added = Assert.Single(commands);
        Assert.Equal("phone_added_command", added!["id"]!.GetValue<string>());
        Assert.Equal("/usr/bin/hostname", added["command"]!.GetValue<string>());

        // A run resolves the freshly saved command.
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);
        var run = await host.RunAsync("phone_added_command");
        Assert.True(run.Success);
        Assert.Equal(
            "CONDA_EXE=/home/lixr/miniconda3/bin/conda /usr/bin/hostname --file1 '" +
            host.Transport.Session.Uploads[0].Path + "' --file2 '" + host.Transport.Session.Uploads[1].Path + "'",
            host.Transport.Session.CommandLine);
    }

    [Fact]
    public async Task AnInvalidDocumentLeavesTheExistingFileAndCatalogUntouched()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var before = await File.ReadAllTextAsync(CommandsPath(host));

        const string broken = """
            commands:
              - id: broken
                label: "Broken"
                command: "/usr/bin/true"
                type: "not-a-real-type"
            """;
        var result = await SaveAsync(host, broken);
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.False(result.Success);
        Assert.Equal(RemoteCommandsAndroidErrorCodes.ValidationFailed, result.Error?.Code);
        Assert.False(payload["saved"]!.GetValue<bool>());
        Assert.Contains("shell or py", payload["saveError"]!.GetValue<string>(), StringComparison.Ordinal);

        // Nothing was written and nothing was reloaded.
        Assert.Equal(before, await File.ReadAllTextAsync(CommandsPath(host)));
        var catalog = RemoteCommandsTestHost.Parse(await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-catalog", RemoteCommandsAndroidOptions.CommandCatalog, new JsonObject()),
            CancellationToken.None));
        Assert.Equal(3, catalog["commands"]!.AsArray().Count);
        Assert.Contains(
            catalog["commands"]!.AsArray(),
            item => item!["id"]!.GetValue<string>() == "decode_stack");
    }

    [Fact]
    public async Task DocumentsWithoutACommandsSectionOrWithDuplicateIdsAreRejected()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var before = await File.ReadAllTextAsync(CommandsPath(host));

        foreach (var invalid in new[]
                 {
                     "",
                     "types:\n  - shell\n",
                     """
                     commands:
                       - id: dup
                         command: "/usr/bin/true"
                         type: "shell"
                       - id: dup
                         command: "/usr/bin/false"
                         type: "shell"
                     """
                 })
        {
            var result = await SaveAsync(host, invalid);
            Assert.False(result.Success);
            Assert.Equal(RemoteCommandsAndroidErrorCodes.ValidationFailed, result.Error?.Code);
            Assert.False(RemoteCommandsTestHost.Parse(result)["saved"]!.GetValue<bool>());
            Assert.Equal(before, await File.ReadAllTextAsync(CommandsPath(host)));
        }
    }

    [Fact]
    public async Task AMissingContentArgumentIsRejectedWithoutWriting()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var before = await File.ReadAllTextAsync(CommandsPath(host));

        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-save", RemoteCommandsAndroidOptions.CommandCatalogSave, new JsonObject()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("content", result.Error?.Message ?? "", StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(CommandsPath(host)));
    }

    [Fact]
    public async Task APathArgumentCannotRedirectTheWrite()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var escaped = Path.Combine(host.DataRoot, "..", "escaped-commands.yaml");

        var result = await SaveAsync(
            host,
            Replacement,
            new JsonObject
            {
                ["path"] = escaped,
                ["file"] = "/tmp/evil.yaml",
                ["commandsPath"] = escaped
            });

        Assert.True(result.Success);
        Assert.False(File.Exists(escaped));
        Assert.False(File.Exists("/tmp/evil.yaml"));
        Assert.Equal(Replacement, await File.ReadAllTextAsync(CommandsPath(host)));
        Assert.Equal(
            CommandsPath(host),
            RemoteCommandsTestHost.Parse(result)["commandsPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task SavingPublishesACatalogEventCarryingNoDocumentBody()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

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

        await SaveAsync(host, Replacement);
        await host.Module.DisposeAsync(CancellationToken.None);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        var saved = Assert.Single(events.Where(item => item.Type == "catalog.saved"));
        Assert.Equal(1, saved.Payload["commandCount"]!.GetValue<int>());
        Assert.Equal(Replacement.Length, saved.Payload["contentLength"]!.GetValue<int>());

        // The event reports what happened; the document body itself is only in the argument and file.
        Assert.DoesNotContain(
            Replacement,
            saved.Payload.ToJsonString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "phone_added_command",
            saved.Payload.ToJsonString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingPreservesTheOtherSharedFilesAndTheirContent()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);
        var hostsBefore = await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.HostsPath(host.DataRoot));
        var keysBefore = await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.HostKeysPath(host.DataRoot));

        Assert.True((await SaveAsync(host, Replacement)).Success);

        Assert.Equal(hostsBefore, await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.HostsPath(host.DataRoot)));
        Assert.Equal(keysBefore, await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.HostKeysPath(host.DataRoot)));
        Assert.Equal(
            RemoteCommandsAndroidOptions.HostsFileName,
            Path.GetFileName(RemoteCommandsAndroidPaths.HostsPath(host.DataRoot)));
    }

    [Fact]
    public async Task TheDefaultDocumentIsRestorableThroughTheSameCommand()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        Assert.True((await SaveAsync(host, Replacement)).Success);

        var result = await SaveAsync(host, RemoteCommands.Surface.Services.RemoteCommandsYaml.DefaultCommandsYaml);
        Assert.True(result.Success);

        var catalog = RemoteCommandsTestHost.Parse(await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-catalog", RemoteCommandsAndroidOptions.CommandCatalog, new JsonObject()),
            CancellationToken.None));
        Assert.Contains(
            catalog["commands"]!.AsArray(),
            item => item!["id"]!.GetValue<string>() == "decode_stack");
    }
}
