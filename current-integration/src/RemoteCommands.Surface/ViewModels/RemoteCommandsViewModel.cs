using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;
using RemoteCommands.Surface.Views;

namespace RemoteCommands.Surface.ViewModels;

public sealed class RemoteCommandsViewModel : MptObservableViewModel
{
    private readonly RemoteCommandsStore _store;
    private readonly SshCommandExecutor _executor = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<RemoteCommandDefinition> _commands = [];
    private int _selectedCommandIndex;
    private string _host = "";
    private string _input1 = "";
    private string _input2 = "";
    private string _output = "";
    private string _statusText = "Idle";
    private string _statusKind = "idle";
    private bool _isRunning;
    private bool _isSecondInputVisible;
    private string _historySearch = "";
    private IReadOnlyList<RemoteCommandHistoryItem> _historyItems = [];
    private RemoteCommandDefinition? _lastCommand;
    private string _lastInput1 = "";
    private string _lastInput2 = "";
    private RemoteCommandsSettings _settings;

    public RemoteCommandsViewModel(MptAvaloniaSurfaceContext context)
    {
        _store = new RemoteCommandsStore(context.DataDirectory);
        _settings = _store.LoadSettings();
        _host = _settings.LastHost;
        _isSecondInputVisible = _settings.TwoPane;
        _selectedCommandIndex = _settings.LastCommandIndex;
    }

    public IReadOnlyList<RemoteCommandDefinition> Commands => _commands;

    public RemoteCommandDefinition? SelectedCommand =>
        _selectedCommandIndex >= 0 && _selectedCommandIndex < _commands.Count
            ? _commands[_selectedCommandIndex]
            : null;

    public int SelectedCommandIndex
    {
        get => _selectedCommandIndex;
        set
        {
            if (SetProperty(ref _selectedCommandIndex, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(SelectedCommand));
            }
        }
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string Input1
    {
        get => _input1;
        set => SetProperty(ref _input1, value);
    }

    public string Input2
    {
        get => _input2;
        set => SetProperty(ref _input2, value);
    }

    public string Output
    {
        get => _output;
        set => SetProperty(ref _output, value);
    }

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
                OnPropertyChanged(nameof(CanRerun));
            }
        }
    }

    public bool IsSecondInputVisible
    {
        get => _isSecondInputVisible;
        set => SetProperty(ref _isSecondInputVisible, value);
    }

    public string HistorySearch
    {
        get => _historySearch;
        set
        {
            if (SetProperty(ref _historySearch, value))
            {
                OnPropertyChanged(nameof(FilteredHistoryItems));
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
                    item.Host.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public bool CanRerun => !IsRunning && _lastCommand is not null;

    public async Task InitializeAsync()
    {
        await Task.Run(() => _store.EnsureInitialized()).ConfigureAwait(true);
        ReloadCommands();
        ReloadHistory();
        if (_selectedCommandIndex >= _commands.Count && _commands.Count > 0)
        {
            SelectedCommandIndex = 0;
        }
    }

    public void ReloadCommands()
    {
        _commands = _store.LoadCommands();
        OnPropertyChanged(nameof(Commands));
        OnPropertyChanged(nameof(SelectedCommand));
        if (SelectedCommand is null && _commands.Count > 0)
        {
            SelectedCommandIndex = Math.Clamp(_selectedCommandIndex, 0, _commands.Count - 1);
        }
    }

    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var command = SelectedCommand;
        if (command is null)
        {
            SetStatus("error", "No commands available");
            return;
        }

        var host = ResolveHost(command);
        _lastCommand = command;
        _lastInput1 = Input1;
        _lastInput2 = Input2;
        Output = "";
        SetStatus("running", "Running...");
        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            var outputText = await ExecuteAsync(command, host, _cancellation.Token).ConfigureAwait(true);
            Output = outputText;
            _store.AppendHistory(
                new RemoteCommandHistoryItem(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    command.Label,
                    command.Command,
                    command.Type,
                    host,
                    _lastInput1,
                    _lastInput2,
                    IsSecondInputVisible,
                    outputText),
                _settings.HistoryRetention);
            ReloadHistory();
            SetStatus("complete", $"Complete at {DateTime.Now:HH:mm:ss}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("error", "Cancelled");
        }
        catch (Exception ex)
        {
            Output = string.IsNullOrWhiteSpace(Output)
                ? ex.Message
                : Output + Environment.NewLine + ex.Message;
            SetStatus("error", "Execution failed");
        }
        finally
        {
            IsRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;
            SaveSessionState();
        }
    }

    public async Task RerunAsync()
    {
        if (IsRunning || _lastCommand is null)
        {
            return;
        }

        var index = _commands
            .Select((command, position) => (command, position))
            .FirstOrDefault(pair =>
                string.Equals(pair.command.Id, _lastCommand.Id, StringComparison.OrdinalIgnoreCase))
            .position;
        if (index >= 0 && index < _commands.Count)
        {
            SelectedCommandIndex = index;
        }

        Input1 = _lastInput1;
        Input2 = _lastInput2;
        await RunAsync().ConfigureAwait(true);
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        SetStatus("error", "Cancelling...");
    }

    public string OutputText => Output;

    public void ClearOutput()
    {
        Output = "";
        SetStatus("idle", "Idle");
    }

    public void RestoreHistoryItem(RemoteCommandHistoryItem item)
    {
        var index = _commands
            .Select((command, position) => (command, position))
            .FirstOrDefault(pair =>
                string.Equals(pair.command.Label, item.Label, StringComparison.Ordinal) &&
                string.Equals(pair.command.Command, item.Command, StringComparison.Ordinal) &&
                string.Equals(pair.command.Type, item.Type, StringComparison.OrdinalIgnoreCase))
            .position;
        if (index >= 0 && index < _commands.Count)
        {
            SelectedCommandIndex = index;
        }

        Input1 = item.Input1;
        IsSecondInputVisible = item.SecondInputEnabled || !string.IsNullOrEmpty(item.Input2);
        Input2 = item.Input2;
        Output = item.Output;
        if (!string.IsNullOrWhiteSpace(item.Host))
        {
            Host = item.Host;
        }
    }

    public void ClearHistory()
    {
        _store.ClearHistory();
        ReloadHistory();
    }

    public async Task OpenYamlEditorAsync(Window? owner)
    {
        var dialog = new CommandsYamlEditorDialog(_store.CommandsPath);
        if (owner is not null && await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true)
        {
            ReloadCommands();
        }
    }

    public async Task OpenSettingsAsync(Window? owner)
    {
        var dialog = new SettingsDialog(_settings);
        if (owner is not null && await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true)
        {
            _settings = dialog.Result;
            _store.SaveSettings(_settings);
            if (string.IsNullOrWhiteSpace(Host))
            {
                Host = _settings.DefaultHost;
            }

            IsSecondInputVisible = _settings.TwoPane;
        }
    }

    public void OpenExternalEditor()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.ExternalEditor,
                Arguments = $"\"{_store.CommandsPath}\"",
                UseShellExecute = false
            });
        }
        catch (Exception)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _store.CommandsPath,
                UseShellExecute = true
            });
        }
    }

    public void SaveSessionState()
    {
        _settings = _settings with
        {
            LastHost = Host,
            LastCommandIndex = SelectedCommandIndex,
            TwoPane = IsSecondInputVisible
        };
        _store.SaveSettings(_settings);
    }

    private async Task<string> ExecuteAsync(
        RemoteCommandDefinition command,
        string host,
        CancellationToken cancellationToken)
    {
        if (string.Equals(command.Type, "py", StringComparison.OrdinalIgnoreCase))
        {
            if (!RemoteCommandsTextTransforms.IsKnownTool(command.Command))
            {
                throw new InvalidOperationException(
                    $"Python command tool '{command.Command}' has no C# runtime mapping.");
            }

            return RemoteCommandsTextTransforms.Apply(command.Command, Input1);
        }

        var result = await _executor.RunAsync(
            host,
            command.Command,
            Input1,
            Input2,
            line => Dispatcher.UIThread.Post(() => Output += line + Environment.NewLine),
            cancellationToken).ConfigureAwait(true);
        return result.Output;
    }

    private string ResolveHost(RemoteCommandDefinition command)
    {
        if (!string.IsNullOrWhiteSpace(command.Host))
        {
            return command.Host;
        }

        if (!string.IsNullOrWhiteSpace(Host))
        {
            return Host;
        }

        return _settings.DefaultHost;
    }

    private void ReloadHistory()
    {
        _historyItems = _store.LoadHistory();
        OnPropertyChanged(nameof(HistoryItems));
        OnPropertyChanged(nameof(FilteredHistoryItems));
    }

    private void SetStatus(string kind, string text)
    {
        StatusKind = kind;
        StatusText = text;
    }
}
