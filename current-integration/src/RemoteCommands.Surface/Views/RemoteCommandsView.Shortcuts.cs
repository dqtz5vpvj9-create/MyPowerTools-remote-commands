using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.ViewModels;

namespace RemoteCommands.Surface.Views;

public sealed partial class RemoteCommandsView : IMptShortcutCommandSource
{
    public string ShortcutToolId => "remote-commands";
    public string ShortcutContext => "workspace";
    public IReadOnlyList<MptShortcutCommand> GetShortcutCommands()
    {
        if (ViewModel is not { } vm) return [];
        return
        [
            new("remote-commands.ui.run", vm.RunAsync, () => !vm.IsRunning),
            new("remote-commands.ui.rerun", vm.RerunAsync, () => vm.CanRerun),
            new("remote-commands.ui.cancel", () => { vm.Cancel(); return Task.CompletedTask; }, () => vm.IsRunning),
            new("remote-commands.ui.copy-output", () => CopyOutputForShortcutAsync(vm), () => vm.OutputText.Length > 0),
            new("remote-commands.ui.clear-output", () => { vm.ClearOutput(); return Task.CompletedTask; }, () => !vm.IsRunning),
        ];
    }
    private async Task CopyOutputForShortcutAsync(RemoteCommandsViewModel model)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
            ?? throw new InvalidOperationException("Clipboard is unavailable.");
        await CopyAsync(clipboard, model.OutputText);
    }
}
