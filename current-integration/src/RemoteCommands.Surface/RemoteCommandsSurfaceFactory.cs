using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.ViewModels;
using RemoteCommands.Surface.Views;

namespace RemoteCommands.Surface;

/// <summary>
/// Dotnet-surface factory for the Remote Commands tool. Loaded by the Shell's DotnetSurfaceLoader
/// from this assembly via the tool route manifest fields. Builds the page3-equivalent workspace:
/// command catalog, host/inputs/output, SSH or text-transform execution, history, settings and
/// commands.yaml editing.
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
