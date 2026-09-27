using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCommands.Android;

/// <summary>
/// One servable host: the alias the user already uses in commands.yaml (<c>r743</c>) plus the real
/// address the phone has to dial. Android has no <c>~/.ssh/config</c>, so an alias alone cannot be
/// connected to; the catalog is where the real host, port, user and credential kind live.
///
/// It never contains a password or a private key - only the kind - so the file can be exported,
/// inspected or deleted without leaking anything.
/// </summary>
internal sealed record RemoteCommandHostEntry(
    string Alias,
    string Host,
    int Port,
    string Username,
    SshCredentialKind AuthKind);

/// <summary>
/// Reader/writer for the Android host catalog. Writes go through a temporary file and an atomic move,
/// mirroring the shared RemoteCommandsFile behaviour, so an interrupted edit never truncates it.
/// </summary>
internal sealed class RemoteCommandsAndroidHostCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly List<RemoteCommandHostEntry> _hosts = [];

    public RemoteCommandsAndroidHostCatalog(string path)
    {
        _path = path;
        Reload();
    }

    public string Path => _path;

    public bool LoadFailed { get; private set; }

    public IReadOnlyList<RemoteCommandHostEntry> Hosts => _hosts;

    public void Reload()
    {
        LoadFailed = false;
        _hosts.Clear();
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<HostsDocument>(File.ReadAllText(_path), JsonOptions);
            if (document?.Hosts is null)
            {
                return;
            }

            foreach (var entry in document.Hosts)
            {
                if (TryValidate(entry.Alias, entry.Host, entry.Port, entry.Username, entry.AuthKind, out _, out var normalized))
                {
                    _hosts.Add(normalized);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken catalog must never silently fall back to "some host": the run path then reports
            // that no real host is configured for the alias.
            LoadFailed = true;
            _hosts.Clear();
        }
    }

    /// <summary>
    /// Resolves what the user typed (a commands.yaml alias or a real host name) to a catalog entry.
    /// Returns <see langword="null"/> instead of guessing when the phone has no mapping.
    /// </summary>
    public RemoteCommandHostEntry? Resolve(string? aliasOrHost)
    {
        var value = aliasOrHost?.Trim() ?? "";
        if (value.Length == 0)
        {
            return null;
        }

        foreach (var entry in _hosts)
        {
            if (string.Equals(entry.Alias, value, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        foreach (var entry in _hosts)
        {
            if (string.Equals(entry.Host, value, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    public void Upsert(RemoteCommandHostEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!TryValidate(entry.Alias, entry.Host, entry.Port, entry.Username, entry.AuthKind, out var error, out var normalized))
        {
            throw new ArgumentException(error, nameof(entry));
        }

        var index = _hosts.FindIndex(candidate =>
            string.Equals(candidate.Alias, normalized.Alias, StringComparison.OrdinalIgnoreCase));        if (index >= 0)
        {
            _hosts[index] = normalized;
        }
        else
        {
            _hosts.Add(normalized);
        }

        Save();
    }

    public bool Remove(string alias)
    {
        var removed = _hosts.RemoveAll(entry =>
            string.Equals(entry.Alias, alias?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(new HostsDocument { Hosts = _hosts }, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            LoadFailed = false;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Aliases follow the shared RemoteCommandsStore host rules (no whitespace, no leading dash) so a
    /// commands.yaml host keeps resolving on both platforms.
    /// </summary>
    public static bool TryValidate(
        string? alias,
        string? host,
        int port,
        string? username,
        SshCredentialKind authKind,
        out string error,
        out RemoteCommandHostEntry normalized)
    {
        normalized = new RemoteCommandHostEntry("", "", 22, "", authKind);
        var trimmedAlias = alias?.Trim() ?? "";
        var trimmedHost = host?.Trim() ?? "";
        var trimmedUser = username?.Trim() ?? "";

        if (trimmedAlias.Length == 0 || trimmedAlias.Any(char.IsWhiteSpace) || trimmedAlias.StartsWith('-'))
        {
            error = "别名不能为空，且不能包含空白或以 - 开头。";
            return false;
        }

        if (trimmedHost.Length == 0 ||
            trimmedHost.Any(char.IsWhiteSpace) ||
            trimmedHost.StartsWith('-') ||
            trimmedHost.Contains('/') ||
            trimmedHost.Contains('@'))
        {
            error = "真实主机名（IP 或域名）不能为空，且不能包含空白、/ 或 @。";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "端口必须在 1 到 65535 之间。";
            return false;
        }

        if (trimmedUser.Length == 0 || trimmedUser.Any(char.IsWhiteSpace) || trimmedUser.StartsWith('-'))
        {
            error = "SSH 用户名不能为空，且不能包含空白或以 - 开头。";
            return false;
        }

        error = "";
        normalized = new RemoteCommandHostEntry(trimmedAlias, trimmedHost, port, trimmedUser, authKind);
        return true;
    }

    private sealed class HostsDocument
    {
        [JsonPropertyName("hosts")]
        public List<RemoteCommandHostEntry> Hosts { get; set; } = [];
    }
}
