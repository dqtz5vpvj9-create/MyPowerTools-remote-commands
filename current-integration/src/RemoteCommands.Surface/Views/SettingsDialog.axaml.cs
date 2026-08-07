using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MyPowerTools.UI.Controls;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.Views;

public sealed partial class SettingsDialog : Window
{
    private readonly ObservableCollection<string> _hosts = [];
    private readonly RemoteCommandsSettings _originalSettings;

    public SettingsDialog()
        : this(new RemoteCommandsSettings("r743", "code", false, 500, "", 0))
    {
    }

    public SettingsDialog(RemoteCommandsSettings settings)
    {
        AvaloniaXamlLoader.Load(this);
        _originalSettings = settings;

        EditorInput = this.FindControl<MptTextBox>("EditorInput")
            ?? throw new InvalidOperationException("Editor input was not found.");
        RetentionInput = this.FindControl<MptTextBox>("RetentionInput")
            ?? throw new InvalidOperationException("Retention input was not found.");
        TwoPaneInput = this.FindControl<MptCheckBox>("TwoPaneInput")
            ?? throw new InvalidOperationException("Two-pane input was not found.");
        NewHostInput = this.FindControl<MptTextBox>("NewHostInput")
            ?? throw new InvalidOperationException("New host input was not found.");
        HostsList = this.FindControl<ListBox>("HostsList")
            ?? throw new InvalidOperationException("Hosts list was not found.");
        DefaultHostInput = this.FindControl<ComboBox>("DefaultHostInput")
            ?? throw new InvalidOperationException("Default host selector was not found.");
        ErrorText = this.FindControl<TextBlock>("ErrorText")
            ?? throw new InvalidOperationException("Settings error text was not found.");

        foreach (var host in RemoteCommandsStore.ParseKnownHosts(settings.KnownHosts))
        {
            _hosts.Add(host);
        }

        AddHostIfMissing(settings.DefaultHost);
        AddHostIfMissing(settings.LastHost);
        if (_hosts.Count == 0)
        {
            _hosts.Add("r743");
        }

        HostsList.ItemsSource = _hosts;
        DefaultHostInput.ItemsSource = _hosts;
        DefaultHostInput.SelectedItem = _hosts.FirstOrDefault(host =>
            string.Equals(host, settings.DefaultHost, StringComparison.OrdinalIgnoreCase)) ?? _hosts[0];
        EditorInput.Text = settings.ExternalEditor;
        RetentionInput.Text = settings.HistoryRetention.ToString();
        TwoPaneInput.IsChecked = settings.TwoPane;
        Result = settings;
    }

    public RemoteCommandsSettings Result { get; private set; }

    private void OnAddHostClick(object? sender, RoutedEventArgs e)
    {
        if (TryAddPendingHost(selectAddedHost: true))
        {
            HideError();
        }
    }

    private void OnRemoveHostClick(object? sender, RoutedEventArgs e)
    {
        if (HostsList.SelectedItem is not string selected)
        {
            ShowError("Select a host to remove.");
            return;
        }

        if (_hosts.Count == 1)
        {
            ShowError("At least one SSH host must remain available.");
            return;
        }

        var removedDefault = string.Equals(
            DefaultHostInput.SelectedItem as string,
            selected,
            StringComparison.OrdinalIgnoreCase);
        _hosts.Remove(selected);
        if (removedDefault)
        {
            DefaultHostInput.SelectedItem = _hosts[0];
        }

        HideError();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NewHostInput.Text) && !TryAddPendingHost(selectAddedHost: false))
        {
            return;
        }

        if (DefaultHostInput.SelectedItem is not string defaultHost)
        {
            ShowError("Choose a default SSH host.");
            return;
        }

        if (!int.TryParse(RetentionInput.Text, out var retention) || retention is < 10 or > 5000)
        {
            ShowError("History retention must be an integer from 10 through 5000.");
            return;
        }

        Result = _originalSettings with
        {
            DefaultHost = defaultHost,
            ExternalEditor = string.IsNullOrWhiteSpace(EditorInput.Text)
                ? "code"
                : EditorInput.Text.Trim(),
            TwoPane = TwoPaneInput.IsChecked == true,
            HistoryRetention = retention,
            KnownHosts = RemoteCommandsStore.SerializeKnownHosts(_hosts)
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private bool TryAddPendingHost(bool selectAddedHost)
    {
        var candidate = NewHostInput.Text?.Trim() ?? "";
        if (!RemoteCommandsStore.IsValidHost(candidate))
        {
            ShowError("A host must be an OpenSSH alias or destination without whitespace and cannot start with '-'.");
            return false;
        }

        var existing = _hosts.FirstOrDefault(host =>
            string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _hosts.Add(candidate);
            existing = candidate;
        }

        if (selectAddedHost)
        {
            HostsList.SelectedItem = existing;
        }

        if (DefaultHostInput.SelectedItem is null)
        {
            DefaultHostInput.SelectedItem = existing;
        }

        NewHostInput.Text = "";
        return true;
    }

    private void AddHostIfMissing(string? host)
    {
        if (!RemoteCommandsStore.IsValidHost(host) ||
            _hosts.Any(existing => string.Equals(existing, host, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _hosts.Add(host!.Trim());
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError()
    {
        ErrorText.Text = "";
        ErrorText.IsVisible = false;
    }
}
