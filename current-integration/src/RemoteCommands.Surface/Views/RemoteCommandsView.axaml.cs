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
        if (_viewModel is { } previous)
        {
            previous.DialogPresenter = null;
        }

        _viewModel = DataContext as RemoteCommandsViewModel;
        if (_viewModel is { } viewModel)
        {
            // Desktop hosts resolve a Window inside the presenter; single-view hosts (Android) get
            // the in-surface sheet because no platform window exists.
            viewModel.DialogPresenter = new RemoteCommandsDialogSheetPresenter(this);
        }
    }

    /// <summary>
    /// Presents Remote Commands dialogs. Desktop keeps <see cref="Window.ShowDialog{TResult}(Window)"/>;
    /// single-view hosts host the same dialog instance inside the surface sheet.
    /// </summary>
    private sealed class RemoteCommandsDialogSheetPresenter(RemoteCommandsView view) : IRemoteCommandsDialogPresenter
    {
        public Task<bool?> ShowDialogAsync(Window dialog, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TopLevel.GetTopLevel(view) is Window owner
                ? dialog.ShowDialog<bool?>(owner)
                : view.ShowDialogSheetAsync(dialog);
        }
    }

    private TaskCompletionSource<object?>? _sheetCompletion;

    /// <summary>
    /// Hosts a dialog inside the surface when the host has no platform window. The dialog keeps its
    /// own handlers and completes through <see cref="IMptSurfaceSheetDialog"/> instead of Close().
    /// </summary>
    private async Task<bool?> ShowDialogSheetAsync(Window dialog)
    {
        var host = this.FindControl<ContentControl>("DialogSheetHost");
        var sheet = this.FindControl<Border>("DialogSheet");
        var title = this.FindControl<TextBlock>("DialogSheetTitle");
        if (host is null || sheet is null || title is null ||
            dialog is not IMptSurfaceSheetDialog sheetDialog ||
            dialog.Content is not Control body)
        {
            return null;
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sheetCompletion = completion;
        sheetDialog.SheetCompletion = completion;
        dialog.Content = null;
        if (body.DataContext is null)
        {
            body.DataContext = dialog.DataContext;
        }

        title.Text = dialog.Title ?? "";
        host.Content = body;
        sheet.IsVisible = true;
        try
        {
            return await completion.Task as bool?;
        }
        finally
        {
            _sheetCompletion = null;
            sheet.IsVisible = false;
            host.Content = null;
            sheetDialog.SheetCompletion = null;
            dialog.Content = body;
        }
    }

    /// <summary>Closes an open sheet, reporting <paramref name="result"/> to its awaiter.</summary>
    private void CloseDialogSheet(object? result) => _sheetCompletion?.TrySetResult(result);

    private void OnCloseDialogSheetClick(object? sender, RoutedEventArgs e) => CloseDialogSheet(false);

    private void OnDialogSheetKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseDialogSheet(false);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        CloseDialogSheet(false);
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
        if (ViewModel is { } viewModel)
        {
            // The owner may be null on single-view hosts; the presenter then uses the surface sheet.
            SafeFireAsync(() => viewModel.OpenYamlEditorAsync(TopLevel.GetTopLevel(this) as Window));
        }
    }

    private void OnExternalEditorClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenExternalEditor();
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            // The owner may be null on single-view hosts; the presenter then uses the surface sheet.
            SafeFireAsync(() => viewModel.OpenSettingsAsync(TopLevel.GetTopLevel(this) as Window));
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

/// <summary>
/// Implemented by tool dialogs that can report their result when hosted inside a surface sheet.
/// Single-view hosts (Android) cannot show platform windows, so such a dialog must not call
/// <see cref="Avalonia.Controls.Window.Close(object?)"/> there.
/// </summary>
internal interface IMptSurfaceSheetDialog
{
    TaskCompletionSource<object?>? SheetCompletion { get; set; }
}
