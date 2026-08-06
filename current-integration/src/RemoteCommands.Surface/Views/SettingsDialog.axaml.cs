using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MyPowerTools.UI.Controls;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.Views;

public sealed partial class SettingsDialog : Window
{
    public SettingsDialog()
        : this(new RemoteCommandsSettings("r743", "code", false, 500, "", 0))
    {
    }

    public SettingsDialog(RemoteCommandsSettings settings)
    {
        AvaloniaXamlLoader.Load(this);
        EditorInput = this.FindControl<MptTextBox>("EditorInput")
            ?? throw new InvalidOperationException("Editor input was not found.");
        HostInput = this.FindControl<MptTextBox>("HostInput")
            ?? throw new InvalidOperationException("Host input was not found.");
        RetentionInput = this.FindControl<MptTextBox>("RetentionInput")
            ?? throw new InvalidOperationException("Retention input was not found.");
        TwoPaneInput = this.FindControl<MptCheckBox>("TwoPaneInput")
            ?? throw new InvalidOperationException("Two-pane input was not found.");
        EditorInput.Text = settings.ExternalEditor;
        HostInput.Text = settings.DefaultHost;
        RetentionInput.Text = settings.HistoryRetention.ToString();
        TwoPaneInput.IsChecked = settings.TwoPane;
    }

    public RemoteCommandsSettings Result { get; private set; } = null!;

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionInput.Text, out var retention) || retention is < 10 or > 5000)
        {
            return;
        }

        Result = new RemoteCommandsSettings(
            DefaultHost: string.IsNullOrWhiteSpace(HostInput.Text) ? "r743" : HostInput.Text.Trim(),
            ExternalEditor: string.IsNullOrWhiteSpace(EditorInput.Text) ? "code" : EditorInput.Text.Trim(),
            TwoPane: TwoPaneInput.IsChecked == true,
            HistoryRetention: retention,
            LastHost: "",
            LastCommandIndex: 0);
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
