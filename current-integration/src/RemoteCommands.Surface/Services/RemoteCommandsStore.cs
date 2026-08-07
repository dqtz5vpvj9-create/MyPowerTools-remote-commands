using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCommands.Surface.Services;

public sealed record RemoteCommandsSettings(
    string DefaultHost,
    string ExternalEditor,
    bool TwoPane,
    int HistoryRetention,
    string LastHost,
    int LastCommandIndex,
    string LastCommandId = "",
    bool ShowHistory = true);

public sealed record RemoteCommandHistoryItem(
    string Timestamp,
    string Label,
    string Command,
    string Type,
    string Host,
    string Input1,
    string Input2,
    bool SecondInputEnabled,
    string Output,
    string CommandId = "",
    Dictionary<string, string>? Inputs = null,
    bool Succeeded = true,
    int? ExitCode = null,
    long DurationMilliseconds = 0)
{
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> EffectiveInputs
    {
        get
        {
            if (Inputs is { Count: > 0 })
            {
                return Inputs;
            }

            var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input1"] = Input1 ?? ""
            };
            if (SecondInputEnabled || !string.IsNullOrEmpty(Input2))
            {
                legacy["input2"] = Input2 ?? "";
            }

            return legacy;
        }
    }

    [JsonIgnore]
    public string StatusLabel => Succeeded
        ? ExitCode is null or 0 ? "Succeeded" : $"Exit {ExitCode}"
        : ExitCode is null ? "Failed" : $"Failed · exit {ExitCode}";

    [JsonIgnore]
    public string DurationText => DurationMilliseconds <= 0
        ? ""
        : DurationMilliseconds < 1000
            ? $"{DurationMilliseconds} ms"
            : $"{DurationMilliseconds / 1000d:0.0} s";

    [JsonIgnore]
    public string OutputPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Output))
            {
                return "No output";
            }

            var normalized = Output.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            var lines = normalized.Split('\n').Take(2);
            var preview = string.Join(" ", lines).Trim();
            return preview.Length <= 180 ? preview : preview[..180] + "…";
        }
    }
}

/// <summary>
/// File-backed catalog, settings and execution history. Writes are atomic within the tool data
/// directory so a process termination cannot leave a partially-written JSON or YAML file.
/// </summary>
public sealed class RemoteCommandsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
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
        lock (_gate)
        {
            Directory.CreateDirectory(_dataDirectory);
            if (!File.Exists(_commandsPath))
            {
                WriteTextAtomic(_commandsPath, RemoteCommandsYaml.DefaultCommandsYaml);
            }
        }
    }

    public string LoadCommandsText()
    {
        EnsureInitialized();
        lock (_gate)
        {
            return File.ReadAllText(_commandsPath, Encoding.UTF8);
        }
    }

    public IReadOnlyList<RemoteCommandDefinition> LoadCommands() =>
        RemoteCommandsYaml.ParseCommands(LoadCommandsText());

    public void SaveCommands(string yaml)
    {
        if (!RemoteCommandsYaml.TryValidate(yaml, out var error))
        {
            throw new InvalidDataException(error ?? "Invalid commands.yaml.");
        }

        EnsureInitialized();
        lock (_gate)
        {
            WriteTextAtomic(_commandsPath, yaml);
        }
    }

    public RemoteCommandsSettings LoadSettings()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                    var stored = JsonSerializer.Deserialize<SettingsFile>(json, JsonOptions);
                    if (stored is not null)
                    {
                        return stored.ToSettings();
                    }
                }
            }
            catch (Exception)
            {
                QuarantineCorruptFile(_settingsPath);
            }
        }

        return DefaultSettings;
    }

    public void SaveSettings(RemoteCommandsSettings settings)
    {
        EnsureInitialized();
        var normalized = NormalizeSettings(settings);
        var json = JsonSerializer.Serialize(SettingsFile.FromSettings(normalized), JsonOptions);
        lock (_gate)
        {
            WriteTextAtomic(_settingsPath, json);
        }
    }

    public IReadOnlyList<RemoteCommandHistoryItem> LoadHistory()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    var json = File.ReadAllText(_historyPath, Encoding.UTF8);
                    return (JsonSerializer.Deserialize<List<HistoryFileItem>>(json, JsonOptions) ?? [])
                        .Select(item => item.ToHistory())
                        .ToArray();
                }
            }
            catch (Exception)
            {
                QuarantineCorruptFile(_historyPath);
            }
        }

        return [];
    }

    public void AppendHistory(RemoteCommandHistoryItem item, int retention)
    {
        EnsureInitialized();
        lock (_gate)
        {
            var items = LoadHistoryWithoutLock().ToList();
            items.Insert(0, item);
            var cap = Math.Clamp(retention, 10, 5000);
            if (items.Count > cap)
            {
                items.RemoveRange(cap, items.Count - cap);
            }

            var json = JsonSerializer.Serialize(
                items.Select(HistoryFileItem.FromHistory).ToArray(),
                JsonOptions);
            WriteTextAtomic(_historyPath, json);
        }
    }

    public void ClearHistory()
    {
        EnsureInitialized();
        lock (_gate)
        {
            WriteTextAtomic(_historyPath, "[]");
        }
    }

    private static RemoteCommandsSettings DefaultSettings => new(
        DefaultHost: "r743",
        ExternalEditor: "code",
        TwoPane: false,
        HistoryRetention: 500,
        LastHost: "",
        LastCommandIndex: 0,
        LastCommandId: "",
        ShowHistory: true);

    private static RemoteCommandsSettings NormalizeSettings(RemoteCommandsSettings settings) => settings with
    {
        DefaultHost = string.IsNullOrWhiteSpace(settings.DefaultHost) ? "r743" : settings.DefaultHost.Trim(),
        ExternalEditor = string.IsNullOrWhiteSpace(settings.ExternalEditor) ? "code" : settings.ExternalEditor.Trim(),
        HistoryRetention = Math.Clamp(settings.HistoryRetention, 10, 5000),
        LastHost = settings.LastHost?.Trim() ?? "",
        LastCommandIndex = Math.Max(0, settings.LastCommandIndex),
        LastCommandId = settings.LastCommandId?.Trim() ?? ""
    };

    private List<RemoteCommandHistoryItem> LoadHistoryWithoutLock()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath, Encoding.UTF8);
                return (JsonSerializer.Deserialize<List<HistoryFileItem>>(json, JsonOptions) ?? [])
                    .Select(item => item.ToHistory())
                    .ToList();
            }
        }
        catch (Exception)
        {
            QuarantineCorruptFile(_historyPath);
        }

        return [];
    }

    private void QuarantineCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var quarantine = Path.Combine(
                _dataDirectory,
                $"{Path.GetFileName(path)}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
            File.Move(path, quarantine, overwrite: false);
        }
        catch (Exception)
        {
            // Recovery remains best-effort; defaults are still returned to the caller.
        }
    }

    private static void WriteTextAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, text, Utf8NoBom);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // The destination write already succeeded or the temporary file is unavailable.
            }
        }
    }


    private sealed class HistoryFileItem
    {
        public string? Timestamp { get; set; }
        public string? Label { get; set; }
        public string? Command { get; set; }
        public string? Type { get; set; }
        public string? Host { get; set; }
        public string? Input1 { get; set; }
        public string? Input2 { get; set; }
        public bool SecondInputEnabled { get; set; }
        public string? Output { get; set; }
        public string? CommandId { get; set; }
        public Dictionary<string, string>? Inputs { get; set; }
        public bool? Succeeded { get; set; }
        public int? ExitCode { get; set; }
        public long DurationMilliseconds { get; set; }

        public RemoteCommandHistoryItem ToHistory() => new(
            Timestamp ?? "",
            Label ?? "",
            Command ?? "",
            Type ?? "",
            Host ?? "",
            Input1 ?? "",
            Input2 ?? "",
            SecondInputEnabled,
            Output ?? "",
            CommandId ?? "",
            Inputs is null
                ? null
                : new Dictionary<string, string>(Inputs, StringComparer.OrdinalIgnoreCase),
            Succeeded ?? true,
            ExitCode,
            Math.Max(0, DurationMilliseconds));

        public static HistoryFileItem FromHistory(RemoteCommandHistoryItem item) => new()
        {
            Timestamp = item.Timestamp,
            Label = item.Label,
            Command = item.Command,
            Type = item.Type,
            Host = item.Host,
            Input1 = item.Input1,
            Input2 = item.Input2,
            SecondInputEnabled = item.SecondInputEnabled,
            Output = item.Output,
            CommandId = item.CommandId,
            Inputs = item.Inputs,
            Succeeded = item.Succeeded,
            ExitCode = item.ExitCode,
            DurationMilliseconds = item.DurationMilliseconds
        };
    }

    private sealed class SettingsFile
    {
        public string DefaultHost { get; set; } = "r743";
        public string ExternalEditor { get; set; } = "code";
        public bool TwoPane { get; set; }
        public int HistoryRetention { get; set; } = 500;
        public string LastHost { get; set; } = "";
        public int LastCommandIndex { get; set; }
        public string LastCommandId { get; set; } = "";
        public bool ShowHistory { get; set; } = true;

        public RemoteCommandsSettings ToSettings() => NormalizeSettings(new RemoteCommandsSettings(
            DefaultHost,
            ExternalEditor,
            TwoPane,
            HistoryRetention,
            LastHost ?? "",
            LastCommandIndex,
            LastCommandId ?? "",
            ShowHistory));

        public static SettingsFile FromSettings(RemoteCommandsSettings settings) => new()
        {
            DefaultHost = settings.DefaultHost,
            ExternalEditor = settings.ExternalEditor,
            TwoPane = settings.TwoPane,
            HistoryRetention = settings.HistoryRetention,
            LastHost = settings.LastHost,
            LastCommandIndex = settings.LastCommandIndex,
            LastCommandId = settings.LastCommandId,
            ShowHistory = settings.ShowHistory
        };
    }
}
