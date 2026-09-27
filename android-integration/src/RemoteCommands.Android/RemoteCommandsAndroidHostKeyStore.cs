using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCommands.Android;

/// <summary>
/// A server key the user explicitly confirmed for one host:port. Only the public fingerprint is
/// stored - never key material - and the value is exactly the SHA256 fingerprint SSH.NET computed
/// for the presented key, so no separate hashing scheme exists.
/// </summary>
internal sealed record RemoteCommandTrustedHostKey(
    string Host,
    int Port,
    string HostKeyName,
    string FingerprintSha256,
    string AddedAt);

/// <summary>
/// Trust store for SSH host keys. It is deliberately small and explicit:
/// <list type="bullet">
///   <item>nothing is trusted by default - an empty store rejects every connection, which is what
///   produces the first-use confirmation flow;</item>
///   <item>a confirmed key is only ever written by <see cref="Remember"/> after the caller verified
///   that the user confirmed the exact fingerprint;</item>
///   <item>a changed key is never overwritten in place: the caller must revoke the old entry first, so
///   a rotation cannot be silently accepted by a re-connect.</item>
/// </list>
/// </summary>
internal sealed class RemoteCommandsAndroidHostKeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly List<RemoteCommandTrustedHostKey> _keys = [];

    public RemoteCommandsAndroidHostKeyStore(string path)
    {
        _path = path;
        Reload();
    }

    public string Path => _path;

    public IReadOnlyList<RemoteCommandTrustedHostKey> Keys => _keys;

    public void Reload()
    {
        _keys.Clear();
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<HostKeysDocument>(File.ReadAllText(_path), JsonOptions);
            if (document?.Keys is null)
            {
                return;
            }

            foreach (var key in document.Keys)
            {
                if (!string.IsNullOrWhiteSpace(key.Host) &&
                    key.Port is > 0 and <= 65535 &&
                    !string.IsNullOrWhiteSpace(key.FingerprintSha256))
                {
                    _keys.Add(key);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged trust store must fail closed: no entry means no trusted key.
            _keys.Clear();
        }
    }

    public RemoteCommandTrustedHostKey? Find(string host, int port)
    {
        foreach (var key in _keys)
        {
            if (key.Port == port && string.Equals(key.Host, host?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    public string? FingerprintFor(string host, int port) => Find(host, port)?.FingerprintSha256;

    public void Remember(string host, int port, SshHostKeyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("主机不能为空。", nameof(host));
        }

        _keys.RemoveAll(key => key.Port == port && string.Equals(key.Host, host.Trim(), StringComparison.OrdinalIgnoreCase));
        _keys.Add(new RemoteCommandTrustedHostKey(
            host.Trim(),
            port,
            observation.HostKeyName,
            observation.FingerprintSha256,
            DateTimeOffset.Now.ToString("O")));
        Save();
    }

    public bool Revoke(string host, int port)
    {
        var removed = _keys.RemoveAll(key =>
            key.Port == port && string.Equals(key.Host, host?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    private void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new HostKeysDocument { Keys = _keys }, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed class HostKeysDocument
    {
        [JsonPropertyName("keys")]
        public List<RemoteCommandTrustedHostKey> Keys { get; set; } = [];
    }
}
