using System.Text;
using System.Text.Json;

namespace RemoteCommands.Surface.Services;

public sealed record RemoteCommandsSettings(
    string DefaultHost,
    string ExternalEditor,
    bool TwoPane,
    int HistoryRetention,
    string LastHost,
    int LastCommandIndex,
    string KnownHosts = "r743");

public sealed record RemoteCommandHistoryItem(
    string Timestamp,
    string Label,
    string Command,
    string Type,
    string Host,
    string Input1,
    string Input2,
    bool SecondInputEnabled,
    string Output);

/// <summary>
/// Local JSON persistence for Remote Commands settings and execution history. The original
/// page3 used QSettings + SQLite; this port keeps the same user-visible behavior with files
/// under the tool data directory.
/// </summary>
public sealed class RemoteCommandsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory;
    private readonly string _commandsPath;
    private readonly string _settingsPath;
    private readonly string _historyPath;

    public RemoteCommandsStore(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _commandsPath = Path.Combine(dataDirectory, "commands.yaml");
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _historyPath = Path.Combine(dataDirectory, "history.json");
    }

    public string CommandsPath => _commandsPath;

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(_dataDirectory);
        if (!File.Exists(_commandsPath))
        {
            File.WriteAllText(_commandsPath, RemoteCommandsYaml.DefaultCommandsYaml, Encoding.UTF8);
        }
    }

    public IReadOnlyList<RemoteCommandDefinition> LoadCommands()
    {
        EnsureInitialized();
        return RemoteCommandsYaml.ParseCommands(File.ReadAllText(_commandsPath));
    }

    public void SaveCommands(string yaml)
    {
        if (!RemoteCommandsYaml.TryValidate(yaml, out var error))
        {
            throw new InvalidDataException(error ?? "Invalid commands.yaml.");
        }

        EnsureInitialized();
        File.WriteAllText(_commandsPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public RemoteCommandsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var stored = JsonSerializer.Deserialize<SettingsFile>(json, JsonOptions);
                if (stored is not null)
                {
                    return stored.ToSettings();
                }
            }
        }
        catch (Exception)
        {
            // Corrupt settings fall back to defaults and are rewritten on the next save.
        }

        return DefaultSettings;
    }

    public void SaveSettings(RemoteCommandsSettings settings)
    {
        EnsureInitialized();
        var normalized = NormalizeSettings(settings);
        var json = JsonSerializer.Serialize(SettingsFile.FromSettings(normalized), JsonOptions);
        File.WriteAllText(_settingsPath, json, Encoding.UTF8);
    }

    public IReadOnlyList<RemoteCommandHistoryItem> LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                return JsonSerializer.Deserialize<List<RemoteCommandHistoryItem>>(json, JsonOptions) ?? [];
            }
        }
        catch (Exception)
        {
            // Corrupt history is ignored; a fresh list is written on the next run.
        }

        return [];
    }

    public void AppendHistory(RemoteCommandHistoryItem item, int retention)
    {
        EnsureInitialized();
        var items = LoadHistory().ToList();
        items.Insert(0, item);
        var cap = Math.Clamp(retention, 10, 5000);
        if (items.Count > cap)
        {
            items.RemoveRange(cap, items.Count - cap);
        }

        var json = JsonSerializer.Serialize(items, JsonOptions);
        File.WriteAllText(_historyPath, json, Encoding.UTF8);
    }

    public void ClearHistory()
    {
        EnsureInitialized();
        File.WriteAllText(_historyPath, "[]", Encoding.UTF8);
    }

    public static IReadOnlyList<string> ParseKnownHosts(string? text)
    {
        var hosts = new List<string>();
        foreach (var candidate in (text ?? "")
                     .Replace("\r\n", "\n")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddHost(hosts, candidate);
        }

        return hosts;
    }

    public static string SerializeKnownHosts(IEnumerable<string> hosts)
    {
        var normalized = new List<string>();
        foreach (var host in hosts)
        {
            AddHost(normalized, host);
        }

        return string.Join("\n", normalized);
    }

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();
        return !trimmed.StartsWith("-", StringComparison.Ordinal) &&
               !trimmed.Any(char.IsWhiteSpace);
    }

    private static RemoteCommandsSettings NormalizeSettings(RemoteCommandsSettings settings)
    {
        var defaultHost = IsValidHost(settings.DefaultHost) ? settings.DefaultHost.Trim() : "r743";
        var lastHost = IsValidHost(settings.LastHost) ? settings.LastHost.Trim() : "";
        var hosts = new List<string>();
        AddHost(hosts, defaultHost);
        foreach (var host in ParseKnownHosts(settings.KnownHosts))
        {
            AddHost(hosts, host);
        }

        AddHost(hosts, lastHost);

        return settings with
        {
            DefaultHost = defaultHost,
            ExternalEditor = string.IsNullOrWhiteSpace(settings.ExternalEditor)
                ? "code"
                : settings.ExternalEditor.Trim(),
            HistoryRetention = Math.Clamp(settings.HistoryRetention, 10, 5000),
            LastHost = lastHost,
            LastCommandIndex = Math.Max(0, settings.LastCommandIndex),
            KnownHosts = SerializeKnownHosts(hosts)
        };
    }

    private static void AddHost(List<string> hosts, string? host)
    {
        if (!IsValidHost(host))
        {
            return;
        }

        var normalized = host!.Trim();
        if (!hosts.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(normalized);
        }
    }

    private static RemoteCommandsSettings DefaultSettings => new(
        DefaultHost: "r743",
        ExternalEditor: "code",
        TwoPane: false,
        HistoryRetention: 500,
        LastHost: "",
        LastCommandIndex: 0,
        KnownHosts: "r743");

    private sealed class SettingsFile
    {
        public string DefaultHost { get; set; } = "r743";
        public string ExternalEditor { get; set; } = "code";
        public bool TwoPane { get; set; }
        public int HistoryRetention { get; set; } = 500;
        public string LastHost { get; set; } = "";
        public int LastCommandIndex { get; set; }
        public string KnownHosts { get; set; } = "r743";

        public RemoteCommandsSettings ToSettings() => NormalizeSettings(new RemoteCommandsSettings(
            DefaultHost,
            ExternalEditor,
            TwoPane,
            HistoryRetention,
            LastHost ?? "",
            LastCommandIndex,
            KnownHosts ?? "r743"));

        public static SettingsFile FromSettings(RemoteCommandsSettings settings) => new()
        {
            DefaultHost = settings.DefaultHost,
            ExternalEditor = settings.ExternalEditor,
            TwoPane = settings.TwoPane,
            HistoryRetention = settings.HistoryRetention,
            LastHost = settings.LastHost,
            LastCommandIndex = settings.LastCommandIndex,
            KnownHosts = settings.KnownHosts
        };
    }
}
