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
        ViewModel?.SaveSessionState();
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            _ = viewModel.RunAsync();
        }
    }

    private void OnRerunClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            _ = viewModel.RerunAsync();
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

        _ = CopyAsync(clipboard, text);
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
            _ = viewModel.OpenYamlEditorAsync(owner);
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
            _ = viewModel.OpenSettingsAsync(owner);
        }
    }

    private void OnClearHistoryClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ClearHistory();
    }

    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: RemoteCommandHistoryItem item } && ViewModel is { } viewModel)
        {
            viewModel.RestoreHistoryItem(item);
            e.Handled = true;
        }
    }
}
