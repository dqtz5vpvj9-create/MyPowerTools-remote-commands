using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.Views;

public sealed partial class SettingsDialog : Window
{
    private readonly RemoteCommandsSettings _original;
    private readonly TextBox _editorInput;
    private readonly TextBox _hostInput;
    private readonly TextBox _retentionInput;
    private readonly CheckBox _showHistoryInput;
    private readonly TextBlock _validationText;

    public SettingsDialog()
        : this(new RemoteCommandsSettings("r743", "code", false, 500, "", 0))
    {
    }

    public SettingsDialog(RemoteCommandsSettings settings)
    {
        AvaloniaXamlLoader.Load(this);
        _original = settings;
        Result = settings;
        _editorInput = this.FindControl<TextBox>("EditorInput")
            ?? throw new InvalidOperationException("Editor input was not found.");
        _hostInput = this.FindControl<TextBox>("HostInput")
            ?? throw new InvalidOperationException("Host input was not found.");
        _retentionInput = this.FindControl<TextBox>("RetentionInput")
            ?? throw new InvalidOperationException("Retention input was not found.");
        _showHistoryInput = this.FindControl<CheckBox>("ShowHistoryInput")
            ?? throw new InvalidOperationException("History visibility input was not found.");
        _validationText = this.FindControl<TextBlock>("ValidationText")
            ?? throw new InvalidOperationException("Settings validation text was not found.");

        _editorInput.Text = settings.ExternalEditor;
        _hostInput.Text = settings.DefaultHost;
        _retentionInput.Text = settings.HistoryRetention.ToString();
        _showHistoryInput.IsChecked = settings.ShowHistory;
    }

    public RemoteCommandsSettings Result { get; private set; }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_retentionInput.Text, out var retention) || retention is < 10 or > 5000)
        {
            _validationText.Text = "History retention must be an integer from 10 through 5000.";
            return;
        }

        var defaultHost = string.IsNullOrWhiteSpace(_hostInput.Text)
            ? "r743"
            : _hostInput.Text.Trim();
        if (!RemoteCommandExecutionService.IsValidSshDestination(defaultHost, out var hostError))
        {
            _validationText.Text = hostError ?? "Invalid SSH destination.";
            return;
        }

        Result = _original with
        {
            DefaultHost = defaultHost,
            ExternalEditor = string.IsNullOrWhiteSpace(_editorInput.Text)
                ? "code"
                : _editorInput.Text.Trim(),
            HistoryRetention = retention,
            ShowHistory = _showHistoryInput.IsChecked == true
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
