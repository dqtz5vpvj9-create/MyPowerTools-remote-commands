using Avalonia.Controls;

namespace RemoteCommands.Surface.Services;

/// <summary>
/// Presents the Remote Commands dialogs for the active host.
/// </summary>
/// <remarks>
/// Desktop hosts keep the platform window path (<see cref="Window.ShowDialog{TResult}(Window)"/>).
/// Single-view hosts (Android) have no platform window, so the surface presents the dialog the view
/// model already built inside an in-surface sheet. Because the view model hands over its own dialog
/// instance, both paths share the same settings object, the same commands.yaml path and the same
/// result handling.
/// </remarks>
public interface IRemoteCommandsDialogPresenter
{
    /// <summary>
    /// Shows <paramref name="dialog"/> and completes with its dialog result, or <see langword="null"/>
    /// when the host cannot present it.
    /// </summary>
    Task<bool?> ShowDialogAsync(Window dialog, CancellationToken cancellationToken = default);
}
