using Avalonia.Headless.XUnit;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;
using RemoteCommands.Surface.ViewModels;
using Xunit;

namespace PersonalUx.Tests;

public sealed class PersonalUxRerunTests
{
    [AvaloniaFact]
    public async Task Rerun_restores_the_host_inputs_and_second_pane_from_the_original_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-rerun-" + Guid.NewGuid().ToString("N"));
        try
        {
            var context = new MptAvaloniaSurfaceContext("remote-commands", "workspace", root, "light",
                (_, _, _) => throw new NotSupportedException(), (_, _, _) => Task.CompletedTask, null!, _ => { });
            var vm = new RemoteCommandsViewModel(context);
            await vm.InitializeAsync();
            vm.SelectedCommandIndex = vm.Commands.ToList().FindIndex(command => command.Command == "replace_host_directory");
            vm.Host = "first-host";
            vm.Input1 = "open /home/lixr/aosp_host_working_dir/out";
            vm.Input2 = "second input";
            vm.IsSecondInputVisible = true;
            await vm.RunAsync();
            var output = vm.Output;
            vm.Host = "different-host";
            vm.Input1 = "different input";
            vm.Input2 = "changed";
            vm.IsSecondInputVisible = false;
            await vm.RerunAsync();
            Assert.Equal("first-host", vm.Host);
            Assert.True(vm.IsSecondInputVisible);
            Assert.Equal("second input", vm.Input2);
            Assert.Equal(output, vm.Output);
            Assert.Equal(2, vm.HistoryItems.Count);
            Assert.All(vm.HistoryItems, item => Assert.Equal("first-host", item.Host));
            Assert.All(vm.HistoryItems, item => Assert.True(item.SecondInputEnabled));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
