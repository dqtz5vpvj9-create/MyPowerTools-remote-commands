using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using RemoteCommands.Surface.Services;
using RemoteCommands.Surface.ViewModels;

namespace RemoteCommands.Surface.Views;

public sealed partial class RemoteCommandsView : UserControl
{
    private RemoteCommandsViewModel? _viewModel;

    public RemoteCommandsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private RemoteCommandsViewModel? ViewModel =>
        DataContext as RemoteCommandsViewModel ?? _viewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel = DataContext as RemoteCommandsViewModel;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        if (ViewModel is { } viewModel)
        {
            SafeFireAsync(() => viewModel.SaveSessionStateAsync());
        }
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            SafeFireAsync(() => viewModel.RunAsync());
        }
    }

    private void OnRerunClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            SafeFireAsync(() => viewModel.RerunAsync());
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.Cancel();
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { OutputText: { Length: > 0 } text } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        SafeFireAsync(() => CopyAsync(clipboard, text));
    }

    private static async Task CopyAsync(IClipboard clipboard, string text)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }

    private void OnClearOutputClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ClearOutput();
    }

    private void OnEditYamlClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && TopLevel.GetTopLevel(this) is Window owner)
        {
            SafeFireAsync(() => viewModel.OpenYamlEditorAsync(owner));
        }
    }

    private void OnExternalEditorClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenExternalEditor();
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && TopLevel.GetTopLevel(this) is Window owner)
        {
            SafeFireAsync(() => viewModel.OpenSettingsAsync(owner));
        }
    }

    private void OnClearHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            SafeFireAsync(() => viewModel.ClearHistoryAsync());
        }
    }

    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: RemoteCommandHistoryItem item } && ViewModel is { } viewModel)
        {
            viewModel.RestoreHistoryItem(item);
            e.Handled = true;
        }
    }

    private void OnHistoryLoadClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RemoteCommandHistoryItem item } && ViewModel is { } viewModel)
        {
            viewModel.RestoreHistoryItem(item);
            e.Handled = true;
        }
    }

    private void OnHistoryRunClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RemoteCommandHistoryItem item } ||
            ViewModel is not { IsRunning: false } viewModel)
        {
            return;
        }

        var commandStillExists = viewModel.Commands.Any(command =>
            RemoteCommandHistoryMatcher.Matches(
                command.Label,
                command.Command,
                command.Type,
                item.Label,
                item.Command,
                item.Type));
        viewModel.RestoreHistoryItem(item);
        if (commandStillExists)
        {
            SafeFireAsync(() => viewModel.RunAsync());
        }

        e.Handled = true;
    }

    private static async void SafeFireAsync(Func<Task> action, Action<string>? onError = null)
    {
        try { await action(); }
        catch (Exception ex) { onError?.Invoke(ex.Message); Trace.WriteLine($"Unhandled: {ex}"); }
    }
}

public static class RemoteCommandHistoryMatcher
{
    public static bool Matches(
        string commandLabel,
        string commandText,
        string commandType,
        string historyLabel,
        string historyCommand,
        string historyType)
    {
        return string.Equals(commandLabel, historyLabel, StringComparison.Ordinal) &&
               string.Equals(commandText, historyCommand, StringComparison.Ordinal) &&
               string.Equals(commandType, historyType, StringComparison.OrdinalIgnoreCase);
    }
}
