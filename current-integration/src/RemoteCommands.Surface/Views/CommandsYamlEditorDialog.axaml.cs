using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System.Text.RegularExpressions;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.Views;

public sealed partial class CommandsYamlEditorDialog : Window
{
    private readonly RemoteCommandsStore _store;
    private readonly TextBox _editor;
    private readonly TextBlock _validationText;

    public CommandsYamlEditorDialog()
        : this(new RemoteCommandsStore(Path.Combine(
            Path.GetTempPath(),
            "mpt-remote-commands-designer")))
    {
    }

    public CommandsYamlEditorDialog(RemoteCommandsStore store)
    {
        AvaloniaXamlLoader.Load(this);
        _store = store;
        _editor = this.FindControl<TextBox>("Editor")
            ?? throw new InvalidOperationException("YAML editor was not found.");
        _validationText = this.FindControl<TextBlock>("ValidationText")
            ?? throw new InvalidOperationException("YAML validation text was not found.");
        _editor.Text = _store.LoadCommandsText();
    }

    public bool? Result { get; private set; }

    private void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        ValidateEditor();
    }

    private void OnInsertSshClick(object? sender, RoutedEventArgs e)
    {
        InsertSnippet(RemoteCommandsYaml.SshCommandSnippet);
    }

    private void OnInsertLocalClick(object? sender, RoutedEventArgs e)
    {
        InsertSnippet(RemoteCommandsYaml.LocalCommandSnippet);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var text = _editor.Text ?? "";
        if (!RemoteCommandsYaml.TryValidate(text, out var error))
        {
            _validationText.Text = error ?? "Invalid commands.yaml.";
            return;
        }

        try
        {
            _store.SaveCommands(text);
            Result = true;
            Close(true);
        }
        catch (Exception ex)
        {
            _validationText.Text = ex.Message;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close(false);
    }

    private bool ValidateEditor()
    {
        if (RemoteCommandsYaml.TryValidate(_editor.Text ?? "", out var error))
        {
            var count = RemoteCommandsYaml.ParseCommands(_editor.Text ?? "").Count;
            _validationText.Text = $"Catalog is valid · {count} command(s).";
            return true;
        }

        _validationText.Text = error ?? "Invalid commands.yaml.";
        return false;
    }

    private void InsertSnippet(string snippet)
    {
        var text = _editor.Text ?? "";
        var commands = Regex.Match(
            text,
            @"(?m)^commands\s*:\s*(?:#.*)?\r?$",
            RegexOptions.CultureInvariant);
        if (!commands.Success)
        {
            _validationText.Text = "Add a top-level commands: list before inserting an example.";
            return;
        }

        var listStart = commands.Index + commands.Length;
        var nextTopLevel = Regex.Match(
            text[listStart..],
            @"(?m)^(?![ \t#\r\n])(?:[A-Za-z_][A-Za-z0-9_-]*)\s*:",
            RegexOptions.CultureInvariant);
        var insertionIndex = nextTopLevel.Success
            ? listStart + nextTopLevel.Index
            : text.Length;

        var prefix = text[..insertionIndex].TrimEnd('\r', '\n');
        var suffix = text[insertionIndex..].TrimStart('\r', '\n');
        var builder = prefix + Environment.NewLine + snippet.TrimEnd() + Environment.NewLine;
        if (suffix.Length > 0)
        {
            builder += Environment.NewLine + suffix;
        }

        _editor.Text = builder;
        _editor.CaretIndex = Math.Min(prefix.Length + snippet.Length, _editor.Text.Length);
        _editor.Focus();
        _validationText.Text = "Example inserted. Update its id, paths, labels, and inputs before saving.";
    }
}
