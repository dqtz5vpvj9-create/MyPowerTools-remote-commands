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
    private string _lastHost = "";
    private bool _lastSecondInputVisible;
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
    public string LastRunSummary => _lastCommand is null ? "尚未运行命令" :
        $"重跑 {_lastCommand.Label} · {(string.Equals(_lastCommand.Type, "py", StringComparison.OrdinalIgnoreCase) ? "本地转换" : _lastHost)} · 使用上次输入";

    public async Task InitializeAsync()
    {
        await Task.Run(() => _store.EnsureInitialized()).ConfigureAwait(true);
        await ReloadCommandsAsync().ConfigureAwait(true);
        await ReloadHistoryAsync().ConfigureAwait(true);
        if (_selectedCommandIndex >= _commands.Count && _commands.Count > 0)
        {
            SelectedCommandIndex = 0;
        }
    }

    public async Task ReloadCommandsAsync()
    {
        _commands = await _store.LoadCommandsAsync().ConfigureAwait(true);
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

        await RunCoreAsync(command, ResolveHost(command), Input1, Input2, IsSecondInputVisible).ConfigureAwait(true);
    }

    private async Task RunCoreAsync(
        RemoteCommandDefinition command, string host, string input1, string input2, bool secondInputVisible)
    {
        // Snapshot every execution argument before the first await. Editing the workspace while
        // output streams in must not rewrite history or change what "Rerun" executes.
        _lastCommand = command;
        _lastInput1 = input1;
        _lastInput2 = input2;
        _lastHost = host;
        _lastSecondInputVisible = secondInputVisible;
        OnPropertyChanged(nameof(LastRunSummary));
        Output = "";
        SetStatus("running", "Running...");
        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            var outputText = await ExecuteAsync(command, host, input1, input2, _cancellation.Token).ConfigureAwait(true);
            Output = outputText;
            await _store.AppendHistoryAsync(
                new RemoteCommandHistoryItem(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    command.Label,
                    command.Command,
                    command.Type,
                    host,
                    input1,
                    input2,
                    secondInputVisible,
                    outputText),
                _settings.HistoryRetention).ConfigureAwait(true);
            await ReloadHistoryAsync().ConfigureAwait(true);
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
            await SaveSessionStateAsync().ConfigureAwait(true);
        }
    }

    public async Task RerunAsync()
    {
        if (IsRunning || _lastCommand is null)
        {
            return;
        }

        var index = FindCommandIndex(command =>
            string.Equals(command.Id, _lastCommand.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            SetStatus("error", "该命令已不存在");
            return;
        }

        var command = _lastCommand;
        var host = _lastHost;
        var input1 = _lastInput1;
        var input2 = _lastInput2;
        var secondInputVisible = _lastSecondInputVisible;
        SelectedCommandIndex = index;
        Host = host;
        Input1 = input1;
        Input2 = input2;
        IsSecondInputVisible = secondInputVisible;
        await RunCoreAsync(command, host, input1, input2, secondInputVisible).ConfigureAwait(true);
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
        var index = FindCommandIndex(command =>
            string.Equals(command.Label, item.Label, StringComparison.Ordinal) &&
            string.Equals(command.Command, item.Command, StringComparison.Ordinal) &&
            string.Equals(command.Type, item.Type, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            SelectedCommandIndex = index;
        }
        else
        {
            SetStatus("error", "该命令已不存在");
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

    public async Task ClearHistoryAsync()
    {
        _store.ClearHistory();
        await ReloadHistoryAsync().ConfigureAwait(true);
    }

    public async Task OpenYamlEditorAsync(Window? owner)
    {
        var dialog = new CommandsYamlEditorDialog(_store.CommandsPath);
        if (owner is not null && await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true)
        {
            await ReloadCommandsAsync().ConfigureAwait(true);
        }
    }

    public async Task OpenSettingsAsync(Window? owner)
    {
        var dialog = new SettingsDialog(_settings);
        if (owner is not null && await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true)
        {
            _settings = dialog.Result;
            await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
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

    public async Task SaveSessionStateAsync()
    {
        _settings = _settings with
        {
            LastHost = Host,
            LastCommandIndex = SelectedCommandIndex,
            TwoPane = IsSecondInputVisible
        };
        if (_store.SettingsLoadFailed)
        {
            // settings.json exists but could not be read, so this session runs on defaults.
            // Writing them back would replace the user's file; the settings dialog still saves.
            return;
        }

        await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
    }

    private int FindCommandIndex(Func<RemoteCommandDefinition, bool> predicate)
    {
        for (var index = 0; index < _commands.Count; index++)
        {
            if (predicate(_commands[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private async Task<string> ExecuteAsync(
        RemoteCommandDefinition command,
        string host,
        string input1,
        string input2,
        CancellationToken cancellationToken)
    {
        if (string.Equals(command.Type, "py", StringComparison.OrdinalIgnoreCase))
        {
            if (!RemoteCommandsTextTransforms.IsKnownTool(command.Command))
            {
                throw new InvalidOperationException(
                    $"Python command tool '{command.Command}' has no C# runtime mapping.");
            }

            return RemoteCommandsTextTransforms.Apply(command.Command, input1);
        }

        const int maxOutputLength = 512 * 1024;
        var result = await _executor.RunAsync(
            host,
            command.Command,
            input1,
            input2,
            line => Dispatcher.UIThread.Post(() =>
            {
                var newOutput = Output + line + Environment.NewLine;
                if (newOutput.Length > maxOutputLength)
                    newOutput = "... (output truncated) ...\n" + newOutput.Substring(newOutput.Length - maxOutputLength);
                Output = newOutput;
            }),
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

    private async Task ReloadHistoryAsync()
    {
        _historyItems = await _store.LoadHistoryAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(HistoryItems));
        OnPropertyChanged(nameof(FilteredHistoryItems));
    }

    private void SetStatus(string kind, string text)
    {
        StatusKind = kind;
        StatusText = text;
    }
}
