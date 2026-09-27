namespace RemoteCommands.Android.Tests;

/// <summary>
/// Freezes the host key trust behaviour: nothing is trusted by default, a first use or a rotation
/// requires the user to confirm the exact fingerprint, and a changed key is never silently accepted.
/// </summary>
public sealed class HostKeyTrustTests
{
    [Fact]
    public async Task FirstUseStopsBeforeAuthenticationAndReportsTheFingerprint()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");

        var result = await host.RunAsync("decode_stack");
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.Equal(RemoteCommandRunStates.HostKeyRequired, payload["state"]!.GetValue<string>());
        Assert.False(result.Success);
        Assert.Equal(1, host.Transport.ConnectCount);
        Assert.Null(host.Transport.LastTrustedFingerprint);

        var pending = payload["pendingHostKey"]!.AsObject();
        Assert.Equal(host.Transport.Observation.FingerprintSha256, pending["fingerprint"]!.GetValue<string>());
        Assert.Equal(host.Transport.Observation.HostKeyName, pending["hostKeyName"]!.GetValue<string>());
        Assert.Equal("192.168.22.24", pending["host"]!.GetValue<string>());

        // The rejection must not have created trust as a side effect.
        var keys = await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-status", RemoteCommandsAndroidOptions.CommandHostKeyStatus, new JsonObject()),
            CancellationToken.None);
        Assert.Empty(RemoteCommandsTestHost.Parse(keys)["keys"]!.AsArray());

        // A run that never reaches execution must not upload or execute anything.
        Assert.Empty(host.Transport.Session.Uploads);
        Assert.Null(host.Transport.Session.CommandLine);
    }

    [Fact]
    public async Task ConfirmingTheReportedFingerprintAllowsTheNextRun()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");

        var pending = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack"))["pendingHostKey"]!.AsObject();
        await host.AcceptHostKeyAsync("192.168.22.24", 22, pending["fingerprint"]!.GetValue<string>());

        var result = await host.RunAsync("decode_stack", invocationId: "invoke-run-2");
        var payload = RemoteCommandsTestHost.Parse(result);

        Assert.Equal(RemoteCommandRunStates.Succeeded, payload["state"]!.GetValue<string>());
        Assert.True(result.Success);
        Assert.Equal(host.Transport.Observation.FingerprintSha256, host.Transport.LastTrustedFingerprint);
    }

    [Fact]
    public async Task AChangedKeyIsRejectedAndDoesNotReplaceTheTrustedFingerprint()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");

        var first = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack"))["pendingHostKey"]!.AsObject();
        var trusted = first["fingerprint"]!.GetValue<string>();
        await host.AcceptHostKeyAsync("192.168.22.24", 22, trusted);

        // The server now presents a different key.
        host.Transport.Observation = new SshHostKeyObservation("ssh-rsa", "SHA256:ROTATED", 3072);
        var rotated = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack", invocationId: "invoke-run-2"));
        Assert.Equal(RemoteCommandRunStates.HostKeyRequired, rotated["state"]!.GetValue<string>());
        Assert.Equal("SHA256:ROTATED", rotated["pendingHostKey"]!.AsObject()["fingerprint"]!.GetValue<string>());

        // Re-running does not auto-accept either, and the old fingerprint is still the trusted one.
        var again = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack", invocationId: "invoke-run-3"));
        Assert.Equal(RemoteCommandRunStates.HostKeyRequired, again["state"]!.GetValue<string>());
        Assert.Equal(trusted, host.Transport.LastTrustedFingerprint);

        var status = RemoteCommandsTestHost.Parse(await host.Module.ExecuteCommandAsync(
            new CommandRequest("invoke-status", RemoteCommandsAndroidOptions.CommandHostKeyStatus, new JsonObject()),
            CancellationToken.None));
        Assert.Equal(trusted, status["keys"]!.AsArray()[0]!["fingerprint"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConfirmingAFingerprintTheUserWasNotShownCannotTrustTheServer()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");

        // A "confirm" click carries the fingerprint that was displayed; a different value is stored,
        // but the next handshake still compares against what the server actually presents.
        await host.AcceptHostKeyAsync("192.168.22.24", 22, "SHA256:NOT-THE-SERVER-KEY");
        var result = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack"));

        Assert.Equal(RemoteCommandRunStates.HostKeyRequired, result["state"]!.GetValue<string>());
        Assert.Equal("SHA256:NOT-THE-SERVER-KEY", host.Transport.LastTrustedFingerprint);
    }

    [Fact]
    public async Task RevokingForgetsTheKeyAndForcesConfirmationAgain()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");

        var pending = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack"))["pendingHostKey"]!.AsObject();
        await host.AcceptHostKeyAsync("192.168.22.24", 22, pending["fingerprint"]!.GetValue<string>());

        var revoked = await host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-revoke",
                RemoteCommandsAndroidOptions.CommandHostKeyRevoke,
                new JsonObject { ["hostName"] = "192.168.22.24", ["port"] = 22 }),
            CancellationToken.None);
        Assert.True(revoked.Success);
        Assert.Empty(RemoteCommandsTestHost.Parse(revoked)["keys"]!.AsArray());

        var result = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack", invocationId: "invoke-run-2"));
        Assert.Equal(RemoteCommandRunStates.HostKeyRequired, result["state"]!.GetValue<string>());
        Assert.Null(host.Transport.LastTrustedFingerprint);
    }

    [Fact]
    public async Task TrustedKeysSurviveAModuleRestart()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: "pw");
        var pending = RemoteCommandsTestHost.Parse(await host.RunAsync("decode_stack"))["pendingHostKey"]!.AsObject();
        await host.AcceptHostKeyAsync("192.168.22.24", 22, pending["fingerprint"]!.GetValue<string>());

        var keysPath = RemoteCommandsAndroidPaths.HostKeysPath(host.DataRoot);
        var store = new RemoteCommandsAndroidHostKeyStore(keysPath);
        Assert.Equal(host.Transport.Observation.FingerprintSha256, store.FingerprintFor("192.168.22.24", 22));
    }
}
