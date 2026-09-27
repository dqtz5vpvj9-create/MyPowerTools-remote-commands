namespace RemoteCommands.Android.Tests;

/// <summary>
/// Freezes the credential boundary: secrets only ever live in the platform credential store, never in
/// the module's JSON files, command results, status payloads or events. No new hashing is introduced -
/// the store's reference scheme is the only addressing.
/// </summary>
public sealed class SecretHandlingTests
{
    private const string Password = "s3cr3t-PASSWORD-must-not-be-persisted";
    private const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nSENSITIVE-KEY-MATERIAL\n-----END OPENSSH PRIVATE KEY-----";
    private const string Passphrase = "s3cr3t-PASSPHRASE-must-not-be-persisted";

    private static IEnumerable<string> FilesUnder(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

    [Fact]
    public async Task AHostPasswordNeverLandsOnDiskOrInAnyResult()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: Password);

        Assert.Contains(host.Secrets.Values.Values, value => value == Password);

        foreach (var file in FilesUnder(host.DataRoot))
        {
            var content = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(Password, content, StringComparison.Ordinal);
        }

        // Host listing, status and settings must describe the credential without echoing it.
        foreach (var commandId in new[]
                 {
                     RemoteCommandsAndroidOptions.CommandHostsList,
                     RemoteCommandsAndroidOptions.CommandStatus,
                     RemoteCommandsAndroidOptions.CommandHostKeyStatus
                 })
        {
            var result = await host.Module.ExecuteCommandAsync(
                new CommandRequest("invoke", commandId, new JsonObject()),
                CancellationToken.None);
            Assert.DoesNotContain(Password, result.Output, StringComparison.Ordinal);
        }

        var settings = await host.Module.GetSettingsAsync(CancellationToken.None);
        Assert.DoesNotContain(Password, settings.Values.ToJsonString(), StringComparison.Ordinal);

        // The catalog file names the credential kind, never the value.
        var hostsFile = await File.ReadAllTextAsync(RemoteCommandsAndroidPaths.HostsPath(host.DataRoot));
        Assert.Contains("\"authKind\": \"password\"", hostsFile, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, hostsFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivateKeyAndItsPassphraseNeverLandOnDisk()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(privateKey: PrivateKey, passphrase: Passphrase);

        foreach (var file in FilesUnder(host.DataRoot))
        {
            var content = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("SENSITIVE-KEY-MATERIAL", content, StringComparison.Ordinal);
            Assert.DoesNotContain(Passphrase, content, StringComparison.Ordinal);
        }

        Assert.Contains(host.Secrets.Values.Values, value => value.Contains("SENSITIVE-KEY-MATERIAL", StringComparison.Ordinal));
        Assert.Contains(host.Secrets.Values.Values, value => value == Passphrase);
    }

    [Fact]
    public async Task TheCredentialIsLoadedFromTheStoreForTheConnectionOnly()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: Password);
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);

        await host.RunAsync("decode_stack");

        Assert.NotNull(host.Transport.LastCredential);
        Assert.Equal(SshCredentialKind.Password, host.Transport.LastCredential!.Kind);
        Assert.Equal(Password, host.Transport.LastCredential.Secret);

        // The credential record must not print its own material either.
        Assert.DoesNotContain(Password, host.Transport.LastCredential.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostWithoutACredentialCannotRun()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);

        // The catalog entry exists but the credential was never saved (or was removed): the run must
        // fail with guidance instead of attempting a connection with an empty secret.
        await host.AddHostAsync(privateKey: "-----BEGIN OPENSSH PRIVATE KEY-----\nx\n-----END OPENSSH PRIVATE KEY-----");
        host.Secrets.Clear();

        var result = await host.RunAsync("decode_stack");
        Assert.False(result.Success);
        Assert.Contains("还没有保存私钥", result.Error?.Message ?? "", StringComparison.Ordinal);
        Assert.Equal(0, host.Transport.ConnectCount);
    }

    [Fact]
    public async Task RemovingAHostClearsItsSecrets()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        await host.AddHostAsync(password: Password);
        await host.AcceptHostKeyAsync("192.168.22.24", 22, host.Transport.Observation.FingerprintSha256);
        Assert.NotEmpty(host.Secrets.Values);

        var removed = await host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-remove",
                RemoteCommandsAndroidOptions.CommandHostRemove,
                new JsonObject { ["alias"] = "r743" }),
            CancellationToken.None);
        Assert.True(removed.Success);
        Assert.Empty(host.Secrets.Values);
        Assert.Empty(new RemoteCommandsAndroidHostCatalog(RemoteCommandsAndroidPaths.HostsPath(host.DataRoot)).Hosts);
        Assert.Empty(new RemoteCommandsAndroidHostKeyStore(RemoteCommandsAndroidPaths.HostKeysPath(host.DataRoot)).Keys);
    }

    [Fact]
    public async Task AddingAHostWithoutASecretIsRejectedInsteadOfStoringAnEmptyCredential()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        var result = await host.Module.ExecuteCommandAsync(
            new CommandRequest(
                "invoke-add",
                RemoteCommandsAndroidOptions.CommandHostAdd,
                new JsonObject
                {
                    ["alias"] = "r743",
                    ["hostName"] = "192.168.22.24",
                    ["username"] = "lixr",
                    ["auth"] = "password"
                }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(host.Secrets.Values);
        Assert.Empty(new RemoteCommandsAndroidHostCatalog(RemoteCommandsAndroidPaths.HostsPath(host.DataRoot)).Hosts);
    }

    [Fact]
    public void SecretNamesAreSanitizedToTheStoreContract()
    {
        Assert.Equal("r743", RemoteCommandsAndroidSecrets.SanitizeAlias("r743"));
        Assert.Equal("user-host", RemoteCommandsAndroidSecrets.SanitizeAlias("user@host"));
        Assert.Equal("host.1.2.3.4.password", RemoteCommandsAndroidSecrets.PasswordSecretName("1.2.3.4"));
        Assert.Equal("host.weird-alias-22.privateKey", RemoteCommandsAndroidSecrets.PrivateKeySecretName("weird alias:22"));

        // The store only accepts letters, digits, dot, dash and underscore.
        Assert.Matches("^[A-Za-z0-9._-]+$", RemoteCommandsAndroidSecrets.PrivateKeySecretName("weird alias:22"));
    }

    [Fact]
    public async Task HostCatalogRejectsUnsafeAliasesAndHosts()
    {
        await using var host = await RemoteCommandsTestHost.CreateAsync(TestCommands.SingleShellCommand);
        foreach (var args in new[]
                 {
                     new JsonObject { ["alias"] = "-oProxyCommand=evil", ["hostName"] = "h", ["username"] = "u", ["auth"] = "password", ["password"] = "x" },
                     new JsonObject { ["alias"] = "ok", ["hostName"] = "host with space", ["username"] = "u", ["auth"] = "password", ["password"] = "x" },
                     new JsonObject { ["alias"] = "ok", ["hostName"] = "h", ["username"] = "u", ["auth"] = "password", ["password"] = "x", ["port"] = 0 }
                 })
        {
            var result = await host.Module.ExecuteCommandAsync(
                new CommandRequest("invoke-add", RemoteCommandsAndroidOptions.CommandHostAdd, args),
                CancellationToken.None);
            Assert.False(result.Success);
        }

        Assert.Empty(new RemoteCommandsAndroidHostCatalog(RemoteCommandsAndroidPaths.HostsPath(host.DataRoot)).Hosts);
    }
}
