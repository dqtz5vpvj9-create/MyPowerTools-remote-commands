using Google.Protobuf.WellKnownTypes;
using MyPowerTools.HostControl;

namespace RemoteCommands.Android.Tests;

/// <summary>
/// Regression for the real device payload path.
///
/// On Android the phone page never calls the module directly: the arguments it builds
/// (<c>JsonObject.Int32</c>) are converted to a protobuf <c>Struct</c> by
/// <see cref="JsonStructMapper.ToStruct"/> in the host client and converted back by
/// <see cref="JsonStructMapper.ToJsonObject"/> in the host service. A protobuf <c>Struct</c> has no
/// integer type, so the module sees a double-backed <see cref="System.Text.Json.Nodes.JsonValue"/>.
/// These tests drive the real mapper - not a hand-written fake payload - so a settings save that
/// works in a fake UI test can no longer fail on a device.
/// </summary>
public sealed class HostArgumentWireTests
{
    /// <summary>The exact trip a command argument makes between the surface and the module.</summary>
    private static JsonObject ThroughHostControl(JsonObject args) =>
        JsonStructMapper.ToJsonObject(JsonStructMapper.ToStruct(args));

    private static JsonObject SettingsArgs(int retention, int timeout) => new()
    {
        ["values"] = new JsonObject
        {
            ["defaultHost"] = "lab-host",
            ["knownHosts"] = "lab-host",
            ["historyRetention"] = retention,
            ["condaExecutable"] = "/opt/conda/bin/conda",
            ["commandTimeoutMinutes"] = timeout
        }
    };

    [Fact]
    public void HostControlCarriesIntegersAsDoubleBackedJsonValues()
    {
        var wire = ThroughHostControl(SettingsArgs(300, 45));
        var values = (JsonObject)wire["values"]!;

        // Documents today's host behaviour: the integer the page sent is no longer int-readable.
        Assert.False(values["historyRetention"]!.AsValue().TryGetValue<int>(out _));
        Assert.True(values["historyRetention"]!.AsValue().TryGetValue<double>(out var floating));
        Assert.Equal(300d, floating);
    }

    [Fact]
    public async Task SettingsUpdateSucceedsWithTheRealHostControlPayload()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-settings",
                RemoteCommandsAndroidOptions.CommandSettingsUpdate,
                ThroughHostControl(SettingsArgs(300, 45))),
            CancellationToken.None);

        var payload = RemoteCommandsTestHost.Parse(result);
        Assert.True(result.Success, result.Error?.Message ?? result.Output);
        Assert.True(payload["saved"]!.GetValue<bool>());
        Assert.Equal(300, payload["settings"]!["historyRetention"]!.GetValue<int>());
        Assert.Equal(45, payload["settings"]!["commandTimeoutMinutes"]!.GetValue<int>());

        // Persisted, not just echoed: the shared file holds the clamped-free integer value.
        var settingsFile = JsonNode.Parse(
            await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.SettingsPath(host.DataRoot)))!.AsObject();
        Assert.Equal(300, settingsFile["HistoryRetention"]!.GetValue<int>());
        var preferencesFile = JsonNode.Parse(
            await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.PreferencesPath(host.DataRoot)))!.AsObject();
        Assert.Equal(45, preferencesFile["commandTimeoutMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task HostPortSurvivesTheSameTripInsteadOfFallingBackTo22()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        var args = ThroughHostControl(new JsonObject
        {
            ["alias"] = "lab-host",
            ["hostName"] = "192.168.22.24",
            ["port"] = 2222,
            ["username"] = "lixr",
            ["auth"] = "password",
            ["password"] = "pw"
        });

        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-add", RemoteCommandsAndroidOptions.CommandHostAdd, args),
            CancellationToken.None);
        Assert.True(result.Success, result.Error?.Message ?? result.Output);

        var catalog = new RemoteCommandsAndroidHostCatalog(
            RemoteCommandsAndroidPaths.HostsPath(host.DataRoot));
        Assert.Equal(2222, Assert.Single(catalog.Hosts).Port);

        // The host key confirmation carries the port the same way.
        var accepted = await host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-key",
                RemoteCommandsAndroidOptions.CommandHostKeyAccept,
                ThroughHostControl(new JsonObject
                {
                    ["hostName"] = "192.168.22.24",
                    ["port"] = 2222,
                    ["fingerprint"] = host.Transport.Observation.FingerprintSha256,
                    ["hostKeyName"] = "ssh-ed25519"
                })),
            CancellationToken.None);
        Assert.True(accepted.Success, accepted.Error?.Message ?? accepted.Output);
        Assert.Equal(
            host.Transport.Observation.FingerprintSha256,
            new RemoteCommandsAndroidHostKeyStore(RemoteCommandsAndroidPaths.HostKeysPath(host.DataRoot))
                .FingerprintFor("192.168.22.24", 2222));
    }

    [Theory]
    [InlineData(300, 300)]
    [InlineData(10, 10)]
    [InlineData(5000, 5000)]
    public async Task IntegralNumbersAreAcceptedInEveryShapeARealCallerCanSend(int sent, int expected)
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        // int, long and double are all "the same number" once a host has normalised the Struct; the
        // module must accept each of them.
        var shapes = new JsonObject[]
        {
            new() { ["values"] = new JsonObject { ["historyRetention"] = sent } },
            new() { ["values"] = new JsonObject { ["historyRetention"] = (long)sent } },
            new() { ["values"] = new JsonObject { ["historyRetention"] = (double)sent } },
            new() { ["values"] = new JsonObject { ["historyRetention"] = sent.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
        };

        foreach (var shape in shapes)
        {
            var result = await host.Module.ExecuteCommandAsync(
                new CommandRequest("invoke-settings", RemoteCommandsAndroidOptions.CommandSettingsUpdate, shape),
                CancellationToken.None);
            Assert.True(result.Success, shape.ToJsonString());
            Assert.Equal(
                expected,
                RemoteCommandsTestHost.Parse(result)["settings"]!["historyRetention"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task FractionalNumbersAreStillRejectedInsteadOfBeingRounded()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var before = await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.CommandsPath(host.DataRoot));

        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-settings",
                RemoteCommandsAndroidOptions.CommandSettingsUpdate,
                new JsonObject { ["values"] = new JsonObject { ["historyRetention"] = 300.5 } }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(RemoteCommandsAndroidErrorCodes.ValidationFailed, result.Error?.Code);
        Assert.False(File.Exists(RemoteCommandsAndroidPaths.SettingsPath(host.DataRoot)));
        Assert.Equal(before, await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.CommandsPath(host.DataRoot)));
    }

    [Fact]
    public async Task BooleanAndStringArgumentsAlsoSurviveTheHostTrip()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        var run = await host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-run",
                RemoteCommandsAndroidOptions.CommandRun,
                ThroughHostControl(new JsonObject
                {
                    ["commandId"] = "decode_stack",
                    ["host"] = "r743",
                    ["input1"] = "first input",
                    ["input2"] = "second input",
                    ["secondInput"] = true
                })),
            CancellationToken.None);

        Assert.True(run.Success, run.Error?.Message ?? run.Output);
        Assert.Equal("first input", host.Transport.Session.Uploads[0].Content);
        Assert.Equal("second input", host.Transport.Session.Uploads[1].Content);
    }

    [Fact]
    public void StructMapperKeepsNestedObjectsAndListsUsable()
    {
        var wire = ThroughHostControl(new JsonObject
        {
            ["values"] = new JsonObject { ["historyRetention"] = 300 },
            ["keys"] = new JsonArray("condaExecutable")
        });

        Assert.Equal(300d, ((JsonObject)wire["values"]!)["historyRetention"]!.GetValue<double>());
        Assert.Equal("condaExecutable", wire["keys"]!.AsArray()[0]!.GetValue<string>());
        var roundTripped = JsonStructMapper.ToStruct(wire);
        Assert.True(roundTripped.Fields.ContainsKey("values"));
        Assert.Equal(300d, roundTripped.Fields["values"].StructValue.Fields["historyRetention"].NumberValue);
    }
}
