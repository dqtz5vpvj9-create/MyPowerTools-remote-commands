using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.ViewModels;
using RemoteCommands.Surface.Views;

namespace RemoteCommands.Surface;

/// <summary>
/// Dotnet-surface factory for the Remote Commands catalog workspace.
/// </summary>
public sealed class RemoteCommandsSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var viewModel = new RemoteCommandsViewModel(context);
        var view = new RemoteCommandsView { DataContext = viewModel };
        Dispatcher.UIThread.Post(
            () => _ = viewModel.InitializeAsync(),
            DispatcherPriority.Background);
        return view;
    }
}
