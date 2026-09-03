using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.Views;

public sealed partial class CommandsYamlEditorDialog : Window
{
    private readonly string _path;

    public CommandsYamlEditorDialog()
        : this(Path.Combine(Path.GetTempPath(), "mpt-commands.yaml"))
    {
    }

    public CommandsYamlEditorDialog(string path)
    {
        AvaloniaXamlLoader.Load(this);
        _path = path;
        Editor = this.FindControl<TextBox>("Editor")
            ?? throw new InvalidOperationException("YAML editor was not found.");
        ErrorText = this.FindControl<TextBlock>("ErrorText")
            ?? throw new InvalidOperationException("YAML error text was not found.");
        Editor.Text = File.Exists(path) ? File.ReadAllText(path) : RemoteCommandsYaml.DefaultCommandsYaml;
    }

    public bool? Result { get; private set; }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var text = Editor.Text ?? "";
        if (!RemoteCommandsYaml.TryValidate(text, out var error))
        {
            ErrorText.Text = error ?? "Invalid YAML.";
            ErrorText.IsVisible = true;
            return;
        }

        RemoteCommandsFile.Write(_path, text);
        Result = true;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close(false);
    }
}
