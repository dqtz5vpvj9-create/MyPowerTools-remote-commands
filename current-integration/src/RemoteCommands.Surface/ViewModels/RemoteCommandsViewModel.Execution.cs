using Avalonia.Threading;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.ViewModels;

public sealed partial class RemoteCommandsViewModel
{
    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var command = SelectedCommand;
        if (command is null)
        {
            SetStatus("error", "No commands available");
            return;
        }

        var host = command.UsesRemoteHost ? ResolveHost(command) : "";
        if (command.UsesRemoteHost && !RemoteCommandsStore.IsValidHost(host))
        {
            SetStatus("error", "Choose an SSH host in Settings");
            return;
        }

        _lastCommand = command;
        _lastInput1 = Input1;
        _lastInput2 = Input2;
        Output = "";
        SetStatus("running", "Running...");
        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            var outputText = await ExecuteAsync(command, host, _cancellation.Token).ConfigureAwait(true);
            Output = outputText;
            _store.AppendHistory(
                new RemoteCommandHistoryItem(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    command.Label,
                    command.Command,
                    command.Type,
                    host,
                    _lastInput1,
                    _lastInput2,
                    IsSecondInputVisible,
                    outputText),
                _settings.HistoryRetention);
            ReloadHistory();
            SetStatus("complete", $"Complete at {DateTime.Now:HH:mm:ss}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("error", "Cancelled");
        }
        catch (Exception ex)
        {
            Output = string.IsNullOrWhiteSpace(Output)
                ? ex.Message
                : Output + Environment.NewLine + ex.Message;
            SetStatus("error", "Execution failed");
        }
        finally
        {
            IsRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;
            SaveSessionState();
        }
    }

    public async Task RerunAsync()
    {
        if (IsRunning || _lastCommand is null)
        {
            return;
        }

        var index = FindCommandIndex(command =>
            string.Equals(command.Id, _lastCommand.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            SelectedCommandIndex = index;
        }

        Input1 = _lastInput1;
        Input2 = _lastInput2;
        await RunAsync().ConfigureAwait(true);
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        SetStatus("error", "Cancelling...");
    }

    public void ClearOutput()
    {
        Output = "";
        SetStatus("idle", "Idle");
    }

    private async Task<string> ExecuteAsync(
        RemoteCommandDefinition command,
        string host,
        CancellationToken cancellationToken)
    {
        if (string.Equals(command.Type, "py", StringComparison.OrdinalIgnoreCase))
        {
            if (!RemoteCommandsTextTransforms.IsKnownTool(command.Command))
            {
                throw new InvalidOperationException(
                    $"Python command tool '{command.Command}' has no C# runtime mapping.");
            }

            return RemoteCommandsTextTransforms.Apply(command.Command, Input1);
        }

        var result = await _executor.RunAsync(
            host,
            command.Command,
            Input1,
            Input2,
            line => Dispatcher.UIThread.Post(() => Output += line + Environment.NewLine),
            cancellationToken).ConfigureAwait(true);
        return result.Output;
    }

    private string ResolveHost(RemoteCommandDefinition command)
    {
        if (RemoteCommandsStore.IsValidHost(command.Host))
        {
            return command.Host;
        }

        if (RemoteCommandsStore.IsValidHost(Host))
        {
            return Host;
        }

        return _settings.DefaultHost;
    }

    private void SetStatus(string kind, string text)
    {
        StatusKind = kind;
        StatusText = text;
    }
}
