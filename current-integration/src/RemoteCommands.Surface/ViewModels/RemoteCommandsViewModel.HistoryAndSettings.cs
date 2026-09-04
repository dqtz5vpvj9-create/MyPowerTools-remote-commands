using System.Diagnostics;
using Avalonia.Controls;
using RemoteCommands.Surface.Services;
using RemoteCommands.Surface.Views;

namespace RemoteCommands.Surface.ViewModels;

public sealed partial class RemoteCommandsViewModel
{
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

        Input1 = item.Input1;
        IsSecondInputVisible = item.SecondInputEnabled || !string.IsNullOrEmpty(item.Input2);
        Input2 = item.Input2;
        Output = item.Output;
        if (RemoteCommandsStore.IsValidHost(item.Host))
        {
            Host = item.Host;
            ReloadHostOptions();
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
            var configuredHosts = RemoteCommandsStore.ParseKnownHosts(dialog.Result.KnownHosts);
            var selectedHost = configuredHosts.Contains(_lastUserHost, StringComparer.OrdinalIgnoreCase)
                ? _lastUserHost
                : dialog.Result.DefaultHost;
            _lastUserHost = selectedHost;
            SetHostValue(selectedHost);
            _settings = dialog.Result with
            {
                LastHost = selectedHost,
                LastCommandIndex = SelectedCommandIndex
            };
            _store.SaveSettings(_settings);
            _settings = _store.LoadSettings();
            IsSecondInputVisible = _settings.TwoPane;
            ReloadHostOptions();
            ApplySelectedCommandHost();
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
            LastHost = RemoteCommandsStore.IsValidHost(_lastUserHost)
                ? _lastUserHost
                : _settings.DefaultHost,
            LastCommandIndex = SelectedCommandIndex,
            TwoPane = IsSecondInputVisible
        };
        _store.SaveSettings(_settings);
    }

    private void ReloadHistory()
    {
        _historyItems = _store.LoadHistory();
        OnPropertyChanged(nameof(HistoryItems));
        OnPropertyChanged(nameof(FilteredHistoryItems));
        ReloadHostOptions();
    }
}
