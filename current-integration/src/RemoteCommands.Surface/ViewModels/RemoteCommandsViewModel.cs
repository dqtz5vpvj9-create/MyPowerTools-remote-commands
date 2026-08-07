using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;
using RemoteCommands.Surface.Views;

namespace RemoteCommands.Surface.ViewModels;

public sealed class RemoteCommandsViewModel : MptObservableViewModel
{
    private readonly RemoteCommandsStore _store;
    private readonly RemoteCommandExecutionService _executor = new();
    private readonly Dictionary<string, Dictionary<string, string>> _drafts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _outputGate = new();
    private readonly StringBuilder _outputBuffer = new();

    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<RemoteCommandDefinition> _commands = [];
    private IReadOnlyList<RemoteCommandDefinition> _filteredCommands = [];
    private RemoteCommandDefinition? _selectedCommand;
    private string _commandSearch = "";
    private string _host = "";
    private string _output = "";
    private string _statusText = "Idle";
    private string _statusKind = "idle";
    private bool _isRunning;
    private bool _isHistoryVisible;
    private string _historySearch = "";
    private IReadOnlyList<RemoteCommandHistoryItem> _historyItems = [];
    private RemoteCommandsSettings _settings;
    private string _lastCommandId = "";
    private string _lastHost = "";
    private Dictionary<string, string>? _lastInputs;
    private int _outputVersion;
    private int _outputFlushQueued;
    private bool _initialized;

    public RemoteCommandsViewModel(MptAvaloniaSurfaceContext context)
    {
        _store = new RemoteCommandsStore(context.DataDirectory);
        _settings = _store.LoadSettings();
        _host = _settings.LastHost;
        _isHistoryVisible = _settings.ShowHistory;
    }

    public IReadOnlyList<RemoteCommandDefinition> Commands => _commands;

    public IReadOnlyList<RemoteCommandDefinition> FilteredCommands => _filteredCommands;

    public string CatalogSummary => _filteredCommands.Count == _commands.Count
        ? $"{_commands.Count} commands"
        : $"{_filteredCommands.Count} of {_commands.Count} commands";

    public bool HasFilteredCommands => _filteredCommands.Count > 0;

    public bool HasNoFilteredCommands => !HasFilteredCommands;

    public ObservableCollection<RemoteCommandInputViewModel> Inputs { get; } = [];

    public RemoteCommandDefinition? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (ReferenceEquals(_selectedCommand, value))
            {
                return;
            }

            SaveCurrentDraft();
            if (SetProperty(ref _selectedCommand, value))
            {
                RebuildInputs();
                OnPropertyChanged(nameof(SelectedCommandIndex));
                OnPropertyChanged(nameof(UsesHost));
                OnPropertyChanged(nameof(ResolvedHost));
                OnPropertyChanged(nameof(HostEntry));
                OnPropertyChanged(nameof(IsHostEditable));
                OnPropertyChanged(nameof(RunnerLabel));
                OnPropertyChanged(nameof(CommandMetadata));
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    // Compatibility property retained for callers from the first port and persisted settings.
    public int SelectedCommandIndex
    {
        get => SelectedCommand is null ? -1 : IndexOfCommand(SelectedCommand.Id);
        set
        {
            if (value >= 0 && value < _commands.Count)
            {
                SelectedCommand = _commands[value];
            }
        }
    }

    public string CommandSearch
    {
        get => _commandSearch;
        set
        {
            if (SetProperty(ref _commandSearch, value ?? ""))
            {
                ApplyCommandFilter();
            }
        }
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value ?? ""))
            {
                OnPropertyChanged(nameof(ResolvedHost));
                OnPropertyChanged(nameof(HostEntry));
            }
        }
    }

    public string HostEntry
    {
        get => ResolvedHost;
        set
        {
            if (IsHostEditable)
            {
                Host = value;
            }
        }
    }

    public bool IsHostEditable => UsesHost && string.IsNullOrWhiteSpace(SelectedCommand?.Host);

    public string ResolvedHost
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SelectedCommand?.Host))
            {
                return SelectedCommand.Host;
            }

            if (!string.IsNullOrWhiteSpace(Host))
            {
                return Host.Trim();
            }

            if (!string.IsNullOrWhiteSpace(SelectedCommand?.CatalogDefaultHost))
            {
                return SelectedCommand.CatalogDefaultHost;
            }

            return _settings.DefaultHost;
        }
    }

    public bool UsesHost => SelectedCommand?.UsesHost == true;

    public string RunnerLabel => SelectedCommand?.Runner switch
    {
        RemoteCommandRunners.Ssh => "SSH",
        RemoteCommandRunners.Local => "Local process",
        RemoteCommandRunners.Transform => "Built-in transform",
        _ => ""
    };

    public string CommandMetadata
    {
        get
        {
            if (SelectedCommand is not { } command)
            {
                return "Select a command from the catalog.";
            }

            var parts = new List<string> { command.GroupLabel, RunnerLabel };
            if (command.TimeoutSeconds > 0 && command.Runner != RemoteCommandRunners.Transform)
            {
                parts.Add($"timeout {command.TimeoutSeconds}s");
            }

            if (command.Tags.Count > 0)
            {
                parts.Add(command.TagsText);
            }

            return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public string Output
    {
        get => _output;
        private set
        {
            if (SetProperty(ref _output, value ?? ""))
            {
                OnPropertyChanged(nameof(HasOutput));
            }
        }
    }

    public bool HasOutput => !string.IsNullOrEmpty(Output);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanRerun));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanRun => !IsRunning && SelectedCommand is not null;

    public bool CanCancel => IsRunning;

    public bool CanRerun => !IsRunning && !string.IsNullOrWhiteSpace(_lastCommandId);

    public bool IsHistoryVisible
    {
        get => _isHistoryVisible;
        set => SetProperty(ref _isHistoryVisible, value);
    }

    public string HistorySearch
    {
        get => _historySearch;
        set
        {
            if (SetProperty(ref _historySearch, value ?? ""))
            {
                OnPropertyChanged(nameof(FilteredHistoryItems));
                OnPropertyChanged(nameof(HasFilteredHistoryItems));
                OnPropertyChanged(nameof(HasNoFilteredHistoryItems));
            }
        }
    }

    public IReadOnlyList<RemoteCommandHistoryItem> HistoryItems => _historyItems;

    public IReadOnlyList<RemoteCommandHistoryItem> FilteredHistoryItems
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_historySearch))
            {
                return _historyItems;
            }

            var pattern = _historySearch.Trim();
            return _historyItems
                .Where(item =>
                    item.Label.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                    item.Command.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                    item.Output.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                    item.Host.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                    item.EffectiveInputs.Values.Any(value =>
                        value.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
    }

    public bool HasFilteredHistoryItems => FilteredHistoryItems.Count > 0;

    public bool HasNoFilteredHistoryItems => !HasFilteredHistoryItems;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await Task.Run(_store.EnsureInitialized).ConfigureAwait(true);
            ReloadCommands();
            ReloadHistory();
        }
        catch (Exception ex)
        {
            SetStatus("error", "Catalog could not be loaded");
            ReplaceOutput(ex.Message);
        }
    }

    public void ReloadCommands()
    {
        var previousId = SelectedCommand?.Id;
        IReadOnlyList<RemoteCommandDefinition> loaded;
        try
        {
            loaded = _store.LoadCommands();
        }
        catch (Exception ex)
        {
            SetStatus("error", "commands.yaml is invalid");
            ReplaceOutput(ex.Message);
            return;
        }

        _commands = loaded;
        OnPropertyChanged(nameof(Commands));
        ApplyCommandFilter(selectCommand: false);

        var targetId = previousId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            targetId = _settings.LastCommandId;
        }

        var target = FindCommand(targetId);
        if (target is null && _settings.LastCommandIndex >= 0 && _settings.LastCommandIndex < _commands.Count)
        {
            target = _commands[_settings.LastCommandIndex];
        }

        SelectedCommand = target ?? _commands.FirstOrDefault();
        ApplyCommandFilter(selectCommand: false);
        SetStatus("idle", $"Loaded {_commands.Count} commands");
    }

    public async Task RunAsync()
    {
        if (IsRunning || SelectedCommand is not { } command)
        {
            return;
        }

        var values = Inputs.ToDictionary(
            input => input.Id,
            input => input.Value,
            StringComparer.OrdinalIgnoreCase);
        var missing = command.Inputs
            .Where(input => input.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(input.Id)))
            .Select(input => input.Label)
            .ToArray();
        if (missing.Length > 0)
        {
            SetStatus("error", "Required input missing: " + string.Join(", ", missing));
            return;
        }

        var host = ResolvedHost;
        if (command.UsesHost && !RemoteCommandExecutionService.IsValidSshDestination(host, out var hostError))
        {
            SetStatus("error", hostError ?? "Invalid SSH host");
            return;
        }

        SaveCurrentDraft();
        _lastCommandId = command.Id;
        _lastHost = host;
        _lastInputs = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(CanRerun));

        ResetOutputBuffer();
        SetStatus("running", $"Running {command.Label}…");
        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        var startedAt = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();
        var succeeded = false;
        int? exitCode = null;

        try
        {
            var result = await _executor.RunAsync(
                command,
                host,
                values,
                QueueOutputLine,
                _cancellation.Token).ConfigureAwait(true);
            exitCode = result.ExitCode;
            succeeded = result.ExitCode == 0;
            await Dispatcher.UIThread.InvokeAsync(() => ReplaceOutput(result.Output));
            SetStatus(
                succeeded ? "complete" : "error",
                succeeded
                    ? $"Completed at {DateTime.Now:HH:mm:ss}"
                    : $"Failed with exit code {result.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            AppendOutputLine("Cancelled.");
            SetStatus("error", "Cancelled");
        }
        catch (TimeoutException ex)
        {
            AppendOutputLine(ex.Message);
            SetStatus("error", "Timed out");
        }
        catch (Exception ex)
        {
            AppendOutputLine(ex.Message);
            SetStatus("error", "Execution failed");
        }
        finally
        {
            stopwatch.Stop();
            var finalOutput = SnapshotOutputBuffer();
            await Dispatcher.UIThread.InvokeAsync(() => Output = finalOutput);

            try
            {
                SaveSessionState();
            }
            catch (Exception ex)
            {
                AppendOutputLine($"Could not save settings: {ex.Message}");
            }

            var history = CreateHistoryItem(
                command,
                host,
                values,
                startedAt,
                succeeded,
                exitCode,
                stopwatch.ElapsedMilliseconds,
                SnapshotOutputBuffer());
            try
            {
                await Task.Run(() => _store.AppendHistory(history, _settings.HistoryRetention))
                    .ConfigureAwait(true);
                ReloadHistory();
            }
            catch (Exception ex)
            {
                AppendOutputLine($"Could not save execution history: {ex.Message}");
                SetStatus("error", "Execution finished, but history could not be saved");
            }
            finally
            {
                IsRunning = false;
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }

    public async Task RerunAsync()
    {
        if (IsRunning || string.IsNullOrWhiteSpace(_lastCommandId) || _lastInputs is null)
        {
            return;
        }

        var command = FindCommand(_lastCommandId);
        if (command is null)
        {
            SetStatus("error", "The previous command no longer exists in commands.yaml");
            return;
        }

        SelectedCommand = command;
        if (string.IsNullOrWhiteSpace(command.Host))
        {
            Host = _lastHost;
        }

        ApplyInputValues(_lastInputs);
        await RunAsync().ConfigureAwait(true);
    }

    public void Cancel()
    {
        if (_cancellation is null)
        {
            return;
        }

        _cancellation.Cancel();
        SetStatus("running", "Cancelling…");
    }

    public void ClearOutput()
    {
        ResetOutputBuffer();
        SetStatus("idle", "Idle");
    }

    public void RestoreHistoryItem(RemoteCommandHistoryItem item)
    {
        var command = FindCommand(item.CommandId);
        command ??= _commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, item.Label, StringComparison.Ordinal) &&
            string.Equals(candidate.Command, item.Command, StringComparison.Ordinal) &&
            string.Equals(candidate.Type, item.Type, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            SetStatus("error", "The command used by this history item is no longer in the catalog");
            return;
        }

        SelectedCommand = command;
        ApplyInputValues(item.EffectiveInputs);
        if (string.IsNullOrWhiteSpace(command.Host) && !string.IsNullOrWhiteSpace(item.Host))
        {
            Host = item.Host;
        }

        ReplaceOutput(item.Output);
        SetStatus(item.Succeeded ? "complete" : "error", $"Restored run from {item.Timestamp}");
    }

    public void ClearHistory()
    {
        _store.ClearHistory();
        ReloadHistory();
    }

    public void ToggleHistory()
    {
        IsHistoryVisible = !IsHistoryVisible;
    }

    public async Task OpenYamlEditorAsync(Window? owner)
    {
        if (owner is null)
        {
            return;
        }

        var dialog = new CommandsYamlEditorDialog(_store);
        if (await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true)
        {
            ReloadCommands();
        }
    }

    public async Task OpenSettingsAsync(Window? owner)
    {
        if (owner is null)
        {
            return;
        }

        SaveSessionState();
        var dialog = new SettingsDialog(_settings);
        if (await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true)
        {
            _settings = dialog.Result;
            _store.SaveSettings(_settings);
            IsHistoryVisible = _settings.ShowHistory;
            OnPropertyChanged(nameof(ResolvedHost));
            OnPropertyChanged(nameof(HostEntry));
        }
    }

    public void OpenExternalEditor()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.ExternalEditor,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(_store.CommandsPath);
            Process.Start(startInfo);
        }
        catch (Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _store.CommandsPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SetStatus("error", $"Could not open commands.yaml: {ex.Message}");
            }
        }
    }

    public void SaveSessionState()
    {
        SaveCurrentDraft();
        _settings = _settings with
        {
            LastHost = Host,
            LastCommandIndex = Math.Max(0, SelectedCommandIndex),
            LastCommandId = SelectedCommand?.Id ?? "",
            ShowHistory = IsHistoryVisible
        };
        _store.SaveSettings(_settings);
    }

    public void Shutdown()
    {
        _cancellation?.Cancel();
        try
        {
            SaveSessionState();
        }
        catch (Exception)
        {
            // Surface teardown must continue when the data directory is unavailable.
        }
    }

    private void ApplyCommandFilter(bool selectCommand = true)
    {
        var pattern = CommandSearch.Trim();
        _filteredCommands = string.IsNullOrWhiteSpace(pattern)
            ? _commands
            : _commands.Where(command => CommandMatches(command, pattern)).ToArray();
        OnPropertyChanged(nameof(FilteredCommands));
        OnPropertyChanged(nameof(CatalogSummary));
        OnPropertyChanged(nameof(HasFilteredCommands));
        OnPropertyChanged(nameof(HasNoFilteredCommands));

        if (!selectCommand)
        {
            return;
        }

        if (_filteredCommands.Count == 0)
        {
            SelectedCommand = null;
            return;
        }

        if (SelectedCommand is null || !_filteredCommands.Any(command =>
                string.Equals(command.Id, SelectedCommand.Id, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCommand = _filteredCommands[0];
        }
    }

    private static bool CommandMatches(RemoteCommandDefinition command, string pattern) =>
        command.Label.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
        command.Id.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
        command.GroupLabel.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
        command.Description.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
        command.Command.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
        command.Tags.Any(tag => tag.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private void RebuildInputs()
    {
        Inputs.Clear();
        if (SelectedCommand is not { } command)
        {
            return;
        }

        _drafts.TryGetValue(command.Id, out var draft);
        foreach (var definition in command.Inputs)
        {
            var value = draft?.GetValueOrDefault(definition.Id) ?? definition.DefaultValue;
            Inputs.Add(new RemoteCommandInputViewModel(definition, value));
        }
    }

    private void SaveCurrentDraft()
    {
        if (SelectedCommand is null)
        {
            return;
        }

        _drafts[SelectedCommand.Id] = Inputs.ToDictionary(
            input => input.Id,
            input => input.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyInputValues(IReadOnlyDictionary<string, string> values)
    {
        foreach (var input in Inputs)
        {
            input.Value = values.GetValueOrDefault(input.Id, input.Definition.DefaultValue);
        }

        SaveCurrentDraft();
    }

    private RemoteCommandDefinition? FindCommand(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _commands.FirstOrDefault(command =>
            string.Equals(command.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private int IndexOfCommand(string id)
    {
        for (var index = 0; index < _commands.Count; index++)
        {
            if (string.Equals(_commands[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static RemoteCommandHistoryItem CreateHistoryItem(
        RemoteCommandDefinition command,
        string host,
        IReadOnlyDictionary<string, string> values,
        DateTime startedAt,
        bool succeeded,
        int? exitCode,
        long durationMilliseconds,
        string output)
    {
        var inputs = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        return new RemoteCommandHistoryItem(
            Timestamp: startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            Label: command.Label,
            Command: command.Command,
            Type: command.Type,
            Host: host,
            Input1: inputs.GetValueOrDefault("input1", inputs.Values.FirstOrDefault() ?? ""),
            Input2: inputs.GetValueOrDefault("input2", ""),
            SecondInputEnabled: inputs.Count > 1,
            Output: output,
            CommandId: command.Id,
            Inputs: inputs,
            Succeeded: succeeded,
            ExitCode: exitCode,
            DurationMilliseconds: durationMilliseconds);
    }

    private void ReloadHistory()
    {
        _historyItems = _store.LoadHistory();
        OnPropertyChanged(nameof(HistoryItems));
        OnPropertyChanged(nameof(FilteredHistoryItems));
        OnPropertyChanged(nameof(HasFilteredHistoryItems));
        OnPropertyChanged(nameof(HasNoFilteredHistoryItems));
    }

    private void QueueOutputLine(string line)
    {
        lock (_outputGate)
        {
            _outputBuffer.AppendLine(line);
            _outputVersion++;
        }

        QueueOutputFlush();
    }

    private void QueueOutputFlush()
    {
        if (Interlocked.Exchange(ref _outputFlushQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(FlushQueuedOutput, DispatcherPriority.Background);
    }

    private void FlushQueuedOutput()
    {
        string text;
        int version;
        lock (_outputGate)
        {
            text = _outputBuffer.ToString();
            version = _outputVersion;
        }

        Output = text;
        Interlocked.Exchange(ref _outputFlushQueued, 0);

        lock (_outputGate)
        {
            if (version != _outputVersion)
            {
                QueueOutputFlush();
            }
        }
    }

    private void FlushOutputNow()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            FlushQueuedOutput();
            return;
        }

        Dispatcher.UIThread.Post(FlushQueuedOutput);
    }

    private void AppendOutputLine(string line)
    {
        QueueOutputLine(line);
        FlushOutputNow();
    }

    private string SnapshotOutputBuffer()
    {
        lock (_outputGate)
        {
            return _outputBuffer.ToString();
        }
    }

    private void ReplaceOutput(string text)
    {
        lock (_outputGate)
        {
            _outputBuffer.Clear();
            _outputBuffer.Append(text ?? "");
            _outputVersion++;
        }

        Output = text ?? "";
    }

    private void ResetOutputBuffer()
    {
        ReplaceOutput("");
    }

    private void SetStatus(string kind, string text)
    {
        StatusKind = kind;
        StatusText = text;
    }
}
