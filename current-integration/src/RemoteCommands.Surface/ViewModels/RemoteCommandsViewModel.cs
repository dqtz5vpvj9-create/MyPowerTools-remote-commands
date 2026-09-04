using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.ViewModels;

public sealed partial class RemoteCommandsViewModel : MptObservableViewModel
{
    private readonly RemoteCommandsStore _store;
    private readonly SshCommandExecutor _executor = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<RemoteCommandDefinition> _commands = [];
    private IReadOnlyList<string> _hostOptions = [];
    private int _selectedCommandIndex;
    private string _host = "";
    private string _lastUserHost = "";
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
        _host = RemoteCommandsStore.IsValidHost(_settings.LastHost)
            ? _settings.LastHost
            : _settings.DefaultHost;
        _lastUserHost = _host;
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
            var normalized = _commands.Count == 0
                ? 0
                : Math.Clamp(value, 0, _commands.Count - 1);
            if (normalized == _selectedCommandIndex)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedCommand?.Host) &&
                RemoteCommandsStore.IsValidHost(_host))
            {
                _lastUserHost = _host;
            }

            if (SetProperty(ref _selectedCommandIndex, normalized))
            {
                OnSelectedCommandChanged();
            }
        }
    }

    public IReadOnlyList<string> HostOptions => _hostOptions;

    public string Host
    {
        get => _host;
        set
        {
            var normalized = value?.Trim() ?? "";
            if (SetProperty(ref _host, normalized) &&
                string.IsNullOrWhiteSpace(SelectedCommand?.Host) &&
                RemoteCommandsStore.IsValidHost(normalized))
            {
                _lastUserHost = normalized;
            }
        }
    }

    public bool UsesRemoteHost => SelectedCommand?.UsesRemoteHost == true;

    public bool IsHostSelectionEnabled =>
        UsesRemoteHost &&
        string.IsNullOrWhiteSpace(SelectedCommand?.Host) &&
        !IsRunning;

    public string HostSelectionHint
    {
        get
        {
            if (!UsesRemoteHost)
            {
                return "This command runs locally and does not use SSH.";
            }

            if (!string.IsNullOrWhiteSpace(SelectedCommand?.Host))
            {
                return $"This command always runs on {SelectedCommand.Host}.";
            }

            return "Choose a saved SSH host. Hosts are managed in Settings.";
        }
    }

    public string Input1Label => SelectedCommand?.Input1Label ?? "Input";
    public string Input1Placeholder =>
        SelectedCommand?.Input1Placeholder ?? "Paste or type the command input.";
    public string Input2Label => SelectedCommand?.Input2Label ?? "Additional input";
    public string Input2Placeholder =>
        SelectedCommand?.Input2Placeholder ?? "Optional second input.";

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
                OnPropertyChanged(nameof(IsHostSelectionEnabled));
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
    public string OutputText => Output;

    public async Task InitializeAsync()
    {
        await Task.Run(() => _store.EnsureInitialized()).ConfigureAwait(true);
        ReloadCommands();
        ReloadHistory();
    }

    public void ReloadCommands()
    {
        _commands = _store.LoadCommands();
        _selectedCommandIndex = _commands.Count == 0
            ? 0
            : Math.Clamp(_selectedCommandIndex, 0, _commands.Count - 1);
        OnPropertyChanged(nameof(Commands));
        OnSelectedCommandChanged();
        ReloadHostOptions();
    }
}
