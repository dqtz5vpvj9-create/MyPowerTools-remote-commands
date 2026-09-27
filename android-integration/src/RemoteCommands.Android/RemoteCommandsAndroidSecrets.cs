using System.Text;
using MyPowerTools.Platform.Abstractions;

namespace RemoteCommands.Android;

/// <summary>
/// Credential access for the Android Remote Commands module. Everything secret goes through the
/// platform <c>secret.store</c> (Android Keystore-backed on the phone) and nothing secret is ever
/// written into the module's JSON files, returned by a command, or included in an event payload.
///
/// Secret references are <c>secret://&lt;moduleId&gt;/host.&lt;alias&gt;.&lt;field&gt;</c>. The alias is sanitized
/// because the store only accepts letters, digits, dot, dash and underscore.
/// </summary>
internal sealed class RemoteCommandsAndroidSecrets(ISecretStore store)
{
    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public static string SanitizeAlias(string alias)
    {
        var builder = new StringBuilder(alias.Length);
        foreach (var character in alias.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-');
        }

        return builder.Length == 0 ? "host" : builder.ToString();
    }

    public static string PasswordSecretName(string alias) =>
        $"{RemoteCommandsAndroidOptions.SecretNamePrefix}{SanitizeAlias(alias)}.password";

    public static string PrivateKeySecretName(string alias) =>
        $"{RemoteCommandsAndroidOptions.SecretNamePrefix}{SanitizeAlias(alias)}.privateKey";

    public static string PassphraseSecretName(string alias) =>
        $"{RemoteCommandsAndroidOptions.SecretNamePrefix}{SanitizeAlias(alias)}.passphrase";

    public async Task SavePasswordAsync(string alias, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("密码不能为空。", nameof(password));
        }

        await _store.SaveAsync(
            RemoteCommandsAndroidOptions.ModuleId,
            PasswordSecretName(alias),
            password,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePrivateKeyAsync(
        string alias,
        string privateKey,
        string? passphrase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new ArgumentException("私钥内容不能为空。", nameof(privateKey));
        }

        await _store.SaveAsync(
            RemoteCommandsAndroidOptions.ModuleId,
            PrivateKeySecretName(alias),
            privateKey,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(passphrase))
        {
            await _store.SaveAsync(
                RemoteCommandsAndroidOptions.ModuleId,
                PassphraseSecretName(alias),
                passphrase,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _store.DeleteAsync(
                SecretReference.Create(RemoteCommandsAndroidOptions.ModuleId, PassphraseSecretName(alias)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearAsync(string alias, CancellationToken cancellationToken)
    {
        await _store.DeleteAsync(
            SecretReference.Create(RemoteCommandsAndroidOptions.ModuleId, PasswordSecretName(alias)),
            cancellationToken).ConfigureAwait(false);
        await _store.DeleteAsync(
            SecretReference.Create(RemoteCommandsAndroidOptions.ModuleId, PrivateKeySecretName(alias)),
            cancellationToken).ConfigureAwait(false);
        await _store.DeleteAsync(
            SecretReference.Create(RemoteCommandsAndroidOptions.ModuleId, PassphraseSecretName(alias)),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HasCredentialAsync(RemoteCommandHostEntry entry, CancellationToken cancellationToken) =>
        entry.AuthKind switch
        {
            SshCredentialKind.Password => ExistsAsync(PasswordSecretName(entry.Alias), cancellationToken),
            _ => ExistsAsync(PrivateKeySecretName(entry.Alias), cancellationToken)
        };

    /// <summary>
    /// Loads the credential for one run. The returned value never leaves the transport call.
    /// </summary>
    public async Task<SshCredential> LoadAsync(RemoteCommandHostEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.AuthKind == SshCredentialKind.Password)
        {
            var password = await ReadAsync(PasswordSecretName(entry.Alias), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException($"主机“{entry.Alias}”还没有保存密码，请在主机设置中填写。");
            }

            return new SshCredential(SshCredentialKind.Password, password);
        }

        var key = await ReadAsync(PrivateKeySecretName(entry.Alias), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException($"主机“{entry.Alias}”还没有保存私钥，请在主机设置中导入。");
        }

        var passphrase = await ReadAsync(PassphraseSecretName(entry.Alias), cancellationToken).ConfigureAwait(false) ?? "";
        return new SshCredential(SshCredentialKind.PrivateKey, key, passphrase);
    }

    private async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(await ReadAsync(name, cancellationToken).ConfigureAwait(false));

    private Task<string?> ReadAsync(string name, CancellationToken cancellationToken) =>
        _store.ReadAsync(
            SecretReference.Create(RemoteCommandsAndroidOptions.ModuleId, name),
            cancellationToken);
}
