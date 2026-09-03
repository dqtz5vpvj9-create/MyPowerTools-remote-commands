using System.Diagnostics;
using System.Text.Json;

namespace RemoteCommands.Surface.Services;

public sealed record RemoteCommandsSettings(
    string DefaultHost,
    string ExternalEditor,
    bool TwoPane,
    int HistoryRetention,
    string LastHost,
    int LastCommandIndex);

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

    /// <summary>
    /// True when the last settings read hit a file that exists but could not be used. The
    /// session state is then held back so an unreadable file is never replaced by defaults.
    /// </summary>
    public bool SettingsLoadFailed { get; private set; }

    /// <summary>
    /// True when the last history read hit a file that exists but could not be used, in which
    /// case appending is skipped instead of rewriting the file with a single entry.
    /// </summary>
    public bool HistoryLoadFailed { get; private set; }

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(_dataDirectory);
        if (!File.Exists(_commandsPath))
        {
            RemoteCommandsFile.Write(_commandsPath, RemoteCommandsYaml.DefaultCommandsYaml);
        }
    }

    public IReadOnlyList<RemoteCommandDefinition> LoadCommands()
    {
        EnsureInitialized();
        return RemoteCommandsYaml.ParseCommands(File.ReadAllText(_commandsPath));
    }

    public async Task<IReadOnlyList<RemoteCommandDefinition>> LoadCommandsAsync()
    {
        EnsureInitialized();
        return RemoteCommandsYaml.ParseCommands(
            await File.ReadAllTextAsync(_commandsPath).ConfigureAwait(false));
    }

    public void SaveCommands(string yaml)
    {
        if (!RemoteCommandsYaml.TryValidate(yaml, out var error))
        {
            throw new InvalidDataException(error ?? "Invalid commands.yaml.");
        }

        EnsureInitialized();
        RemoteCommandsFile.Write(_commandsPath, yaml);
    }

    public RemoteCommandsSettings LoadSettings()
    {
        SettingsLoadFailed = false;
        if (!File.Exists(_settingsPath))
        {
            return DefaultSettings;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_settingsPath), JsonOptions);
            if (stored is not null)
            {
                return stored.ToSettings();
            }

            SettingsLoadFailed = true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            SettingsLoadFailed = true;
            Trace.WriteLine(
                $"Remote Commands: settings.json could not be read ({exception.Message}); " +
                "defaults are used for this session and the file is left untouched.");
        }

        return DefaultSettings;
    }

    public void SaveSettings(RemoteCommandsSettings settings)
    {
        EnsureInitialized();
        RemoteCommandsFile.Write(_settingsPath, SerializeSettings(settings));
        SettingsLoadFailed = false;
    }

    public async Task SaveSettingsAsync(RemoteCommandsSettings settings)
    {
        EnsureInitialized();
        await RemoteCommandsFile.WriteAsync(_settingsPath, SerializeSettings(settings)).ConfigureAwait(false);
        SettingsLoadFailed = false;
    }

    public IReadOnlyList<RemoteCommandHistoryItem> LoadHistory()
    {
        HistoryLoadFailed = false;
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            return ReadHistory(File.ReadAllText(_historyPath));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return ReportHistoryReadFailure(exception);
        }
    }

    public async Task<IReadOnlyList<RemoteCommandHistoryItem>> LoadHistoryAsync()
    {
        HistoryLoadFailed = false;
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            return ReadHistory(await File.ReadAllTextAsync(_historyPath).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return ReportHistoryReadFailure(exception);
        }
    }

    public void AppendHistory(RemoteCommandHistoryItem item, int retention)
    {
        EnsureInitialized();
        var items = LoadHistory().ToList();
        if (HistoryLoadFailed)
        {
            return;
        }

        RemoteCommandsFile.Write(_historyPath, SerializeHistory(items, item, retention));
    }

    public async Task AppendHistoryAsync(RemoteCommandHistoryItem item, int retention)
    {
        EnsureInitialized();
        var items = (await LoadHistoryAsync().ConfigureAwait(false)).ToList();
        if (HistoryLoadFailed)
        {
            return;
        }

        await RemoteCommandsFile.WriteAsync(_historyPath, SerializeHistory(items, item, retention))
            .ConfigureAwait(false);
    }

    public void ClearHistory()
    {
        EnsureInitialized();
        RemoteCommandsFile.Write(_historyPath, "[]");
        HistoryLoadFailed = false;
    }

    private static IReadOnlyList<RemoteCommandHistoryItem> ReadHistory(string json) =>
        JsonSerializer.Deserialize<List<RemoteCommandHistoryItem>>(json, JsonOptions) ?? [];

    private IReadOnlyList<RemoteCommandHistoryItem> ReportHistoryReadFailure(Exception exception)
    {
        HistoryLoadFailed = true;
        Trace.WriteLine(
            $"Remote Commands: history.json could not be read ({exception.Message}); " +
            "the file is left untouched until the history is cleared.");
        return [];
    }

    private static string SerializeSettings(RemoteCommandsSettings settings) =>
        JsonSerializer.Serialize(SettingsFile.FromSettings(settings), JsonOptions);

    private static string SerializeHistory(
        List<RemoteCommandHistoryItem> items,
        RemoteCommandHistoryItem item,
        int retention)
    {
        items.Insert(0, item);
        var cap = Math.Clamp(retention, 10, 5000);
        if (items.Count > cap)
        {
            items.RemoveRange(cap, items.Count - cap);
        }

        return JsonSerializer.Serialize(items, JsonOptions);
    }

    private static RemoteCommandsSettings DefaultSettings => new(
        DefaultHost: "r743",
        ExternalEditor: "code",
        TwoPane: false,
        HistoryRetention: 500,
        LastHost: "",
        LastCommandIndex: 0);

    private sealed class SettingsFile
    {
        public string DefaultHost { get; set; } = "r743";
        public string ExternalEditor { get; set; } = "code";
        public bool TwoPane { get; set; }
        public int HistoryRetention { get; set; } = 500;
        public string LastHost { get; set; } = "";
        public int LastCommandIndex { get; set; }

        public RemoteCommandsSettings ToSettings() => new(
            string.IsNullOrWhiteSpace(DefaultHost) ? "r743" : DefaultHost,
            string.IsNullOrWhiteSpace(ExternalEditor) ? "code" : ExternalEditor,
            TwoPane,
            Math.Clamp(HistoryRetention, 10, 5000),
            LastHost ?? "",
            Math.Max(0, LastCommandIndex));

        public static SettingsFile FromSettings(RemoteCommandsSettings settings) => new()
        {
            DefaultHost = settings.DefaultHost,
            ExternalEditor = settings.ExternalEditor,
            TwoPane = settings.TwoPane,
            HistoryRetention = settings.HistoryRetention,
            LastHost = settings.LastHost,
            LastCommandIndex = settings.LastCommandIndex
        };
    }
}
