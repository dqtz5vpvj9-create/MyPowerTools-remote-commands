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
    private readonly TextBox _commandSearchBox;

    public RemoteCommandsView()
    {
        AvaloniaXamlLoader.Load(this);
        _commandSearchBox = this.FindControl<TextBox>("CommandSearchBox")
            ?? throw new InvalidOperationException("Command search box was not found.");
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
        ViewModel?.Shutdown();
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.RunAsync();
        }
    }

    private async void OnRerunClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.RerunAsync();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.Cancel();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { Output: { Length: > 0 } text } ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }

    private void OnClearOutputClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ClearOutput();
    }

    private async void OnEditYamlClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && TopLevel.GetTopLevel(this) is Window owner)
        {
            await viewModel.OpenYamlEditorAsync(owner);
        }
    }

    private void OnExternalEditorClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenExternalEditor();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && TopLevel.GetTopLevel(this) is Window owner)
        {
            await viewModel.OpenSettingsAsync(owner);
        }
    }

    private void OnReloadCatalogClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ReloadCommands();
    }

    private void OnToggleHistoryClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleHistory();
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

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            _commandSearchBox.Focus();
            _commandSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Enter)
        {
            await viewModel.RunAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && viewModel.IsRunning)
        {
            viewModel.Cancel();
            e.Handled = true;
        }
    }
}
