namespace RemoteCommands.Android.Tests;

/// <summary>
/// Freezes the settings.update contract: the phone page saves settings exclusively through this module
/// command, the five editable keys round-trip, and bad input never writes any file.
/// </summary>
public sealed class SettingsUpdateTests
{
    private const string ValidValuesJson =
        """{"defaultHost":"lab-host","knownHosts":"lab-host\nbuild-host","historyRetention":250,"condaExecutable":"/opt/conda/bin/conda","commandTimeoutMinutes":45}""";

    private static Task<MyPowerTools.Abstractions.CommandExecutionResult> UpdateAsync(
        RemoteCommandsTestHost host,
        JsonObject? values) =>
        host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-settings",
                RemoteCommandsAndroidOptions.CommandSettingsUpdate,
                values is null ? new JsonObject() : new JsonObject { ["values"] = values }),
            CancellationToken.None).AsTask();

    private static JsonObject ValidValues() => JsonNode.Parse(ValidValuesJson)!.AsObject();

    private static string SettingsPath(RemoteCommandsTestHost host) =>
        RemoteCommandsAndroidPaths.SettingsPath(host.DataRoot);

    /// <summary>Reads a data file that may legitimately not exist yet.</summary>
    private static async Task<string> ReadIfExistsAsync(string path) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path) : "";

    private static string PreferencesPath(RemoteCommandsTestHost host) =>
        RemoteCommandsAndroidPaths.PreferencesPath(host.DataRoot);

    [Fact]
    public async Task AllFiveEditableKeysRoundTripThroughTheCommand()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        var result = await UpdateAsync(host, ValidValues());
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.True(result.Success);
        Assert.True(payload["saved"]!.GetValue<bool>());
        Assert.Equal(
            new[] { "commandTimeoutMinutes", "condaExecutable", "defaultHost", "historyRetention", "knownHosts" },
            payload["appliedKeys"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());

        var settings = payload["settings"]!.AsObject();
        Assert.Equal("lab-host", settings["defaultHost"]!.GetValue<string>());
        Assert.Equal("lab-host\nbuild-host", settings["knownHosts"]!.GetValue<string>());
        Assert.Equal(250, settings["historyRetention"]!.GetValue<int>());
        Assert.Equal("/opt/conda/bin/conda", settings["condaExecutable"]!.GetValue<string>());
        Assert.Equal(45, settings["commandTimeoutMinutes"]!.GetValue<int>());

        // Preferences and status come back with the same result, and the module settings interface
        // reports the identical document.
        Assert.NotNull(payload["status"]);
        Assert.True(payload["status"]!["transportAvailable"]!.GetValue<bool>());
        var snapshot = await host.Module.GetSettingsAsync(CancellationToken.None);
        Assert.Equal("lab-host", snapshot.Values["defaultHost"]!.GetValue<string>());
        Assert.Equal(45, snapshot.Values["commandTimeoutMinutes"]!.GetValue<int>());
        Assert.Equal(payload["revision"]!.GetValue<long>(), (long)snapshot.Revision);

        // …and the same two files the desktop-compatible store owns were updated. The shared
        // settings.json keeps the desktop property names (PascalCase); the Android preferences file
        // is camelCase.
        var settingsFile = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath(host)))!.AsObject();
        Assert.Equal("lab-host", settingsFile["DefaultHost"]!.GetValue<string>());
        Assert.Equal("lab-host\nbuild-host", settingsFile["KnownHosts"]!.GetValue<string>());
        Assert.Equal(250, settingsFile["HistoryRetention"]!.GetValue<int>());
        var preferencesFile = JsonNode.Parse(await File.ReadAllTextAsync(PreferencesPath(host)))!.AsObject();
        Assert.Equal("/opt/conda/bin/conda", preferencesFile["condaExecutable"]!.GetValue<string>());
        Assert.Equal(45, preferencesFile["commandTimeoutMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task SavedSettingsTakeEffectOnTheNextRun()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(alias: "lab-host", host: "192.168.22.24", password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        Assert.True((await UpdateAsync(host, ValidObjects())).Success);

        var result = await host.RunAsync("decode_stack", host: "lab-host");
        Assert.True(result.Success);
        Assert.StartsWith(
            "CONDA_EXE=/opt/conda/bin/conda ",
            host.Transport.Session.CommandLine!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialUpdatesKeepTheOtherKeys()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        Assert.True((await UpdateAsync(host, ValidValues())).Success);

        var result = await UpdateAsync(host, new JsonObject { ["commandTimeoutMinutes"] = 90 });
        var settings = RemoteCommandsTestHost.Parse(result)["settings"]!.AsObject();

        Assert.True(result.Success);
        Assert.Equal(90, settings["commandTimeoutMinutes"]!.GetValue<int>());
        Assert.Equal("lab-host", settings["defaultHost"]!.GetValue<string>());
        Assert.Equal("/opt/conda/bin/conda", settings["condaExecutable"]!.GetValue<string>());
        Assert.Equal(250, settings["historyRetention"]!.GetValue<int>());
    }

    [Fact]
    public async Task BadInputIsRejectedWithoutWritingEitherFile()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        Assert.True((await UpdateAsync(host, ValidValues())).Success);
        var settingsBefore = await ReadIfExistsAsync(SettingsPath(host));
        var preferencesBefore = await ReadIfExistsAsync(PreferencesPath(host));

        var invalid = new (string Name, JsonObject Values)[]
        {
            ("unknown key", new JsonObject { ["password"] = "hunter2" }),
            ("empty default host", new JsonObject { ["defaultHost"] = "  " }),
            ("host with whitespace", new JsonObject { ["defaultHost"] = "lab host" }),
            ("alias with whitespace", new JsonObject { ["knownHosts"] = "ok\nbad alias" }),
            ("retention below range", new JsonObject { ["historyRetention"] = 5 }),
            ("retention above range", new JsonObject { ["historyRetention"] = 99999 }),
            ("retention wrong type", new JsonObject { ["historyRetention"] = "many" }),
            ("empty conda path", new JsonObject { ["condaExecutable"] = "   " }),
            ("timeout below range", new JsonObject { ["commandTimeoutMinutes"] = 0 }),
            ("timeout above range", new JsonObject { ["commandTimeoutMinutes"] = 5000 }),
            ("timeout wrong type", new JsonObject { ["commandTimeoutMinutes"] = "soon" })
        };

        foreach (var (name, values) in invalid)
        {
            var result = await UpdateAsync(host, values);
            var payload = RemoteCommandsTestHost.Parse(result);

            Assert.False(result.Success, name);
            Assert.Equal(RemoteCommandsAndroidErrorCodes.ValidationFailed, result.Error?.Code);
            Assert.False(payload["saved"]!.GetValue<bool>(), name);
            Assert.True(payload["saveError"]!.GetValue<string>().Length > 0, name);

            // The rejected payload still reports the unchanged current values.
            Assert.True(
                string.Equals("lab-host", payload["settings"]!["defaultHost"]!.GetValue<string>(), StringComparison.Ordinal),
                name);
            Assert.True(45 == payload["settings"]!["commandTimeoutMinutes"]!.GetValue<int>(), name);
        }

        Assert.Equal(settingsBefore, await ReadIfExistsAsync(SettingsPath(host)));
        Assert.Equal(preferencesBefore, await ReadIfExistsAsync(PreferencesPath(host)));

        // A rejected update must not bump the revision either.
        var snapshot = await host.Module.GetSettingsAsync(CancellationToken.None);
        var after = RemoteCommandsTestHost.Parse(await UpdateAsync(host, new JsonObject { ["defaultHost"] = "lab-host" }));
        Assert.Equal((long)snapshot.Revision + 1, after["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task AMissingValuesObjectIsRejectedWithoutWriting()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var before = await ReadIfExistsAsync(SettingsPath(host));

        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-settings", RemoteCommandsAndroidOptions.CommandSettingsUpdate, new JsonObject()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("values", result.Error?.Message ?? "", StringComparison.Ordinal);
        Assert.Equal(before, await ReadIfExistsAsync(SettingsPath(host)));
    }

    [Fact]
    public async Task ReadOnlyKeysAreToleratedSoAFullSnapshotCanRoundTrip()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");

        var values = ValidValues();
        values["hostCount"] = 1;
        values["trustedHostKeyCount"] = 0;
        values["activeRun"] = "idle";

        var result = await UpdateAsync(host, values);
        var settings = RemoteCommandsTestHost.Parse(result)["settings"]!.AsObject();

        Assert.True(result.Success);
        Assert.Equal("lab-host", settings["defaultHost"]!.GetValue<string>());
        Assert.Equal(1, settings["hostCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheSettingsInterfaceAndTheCommandShareOnePersistencePath()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        // Interface first…
        await host.Module.ApplySettingsAsync(
            new MyPowerTools.Abstractions.SettingsSnapshotDocument(
                RemoteCommandsAndroidOptions.ModuleId,
                1,
                new JsonObject { ["condaExecutable"] = "/interface/conda" },
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(
            "/interface/conda",
            RemoteCommandsTestHost.Parse(await UpdateAsync(host, new JsonObject { ["historyRetention"] = 100 }))
                ["settings"]!["condaExecutable"]!.GetValue<string>());

        // …then the command, and the interface sees the command's write.
        Assert.True((await UpdateAsync(host, new JsonObject { ["condaExecutable"] = "/command/conda" })).Success);
        var snapshot = await host.Module.GetSettingsAsync(CancellationToken.None);
        Assert.Equal("/command/conda", snapshot.Values["condaExecutable"]!.GetValue<string>());
    }

    private static JsonObject ValidObjects()
    {
        var values = ValidValues();
        values["commandTimeoutMinutes"] = 15;
        return values;
    }
}
