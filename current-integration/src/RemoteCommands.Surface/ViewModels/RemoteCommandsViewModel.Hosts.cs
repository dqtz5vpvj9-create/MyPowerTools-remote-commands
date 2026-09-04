using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.ViewModels;

public sealed partial class RemoteCommandsViewModel
{
    private void ReloadHostOptions()
    {
        var hosts = new List<string>();
        AddHost(hosts, _settings.DefaultHost);
        foreach (var host in RemoteCommandsStore.ParseKnownHosts(_settings.KnownHosts))
        {
            AddHost(hosts, host);
        }

        foreach (var command in _commands)
        {
            AddHost(hosts, command.Host);
        }

        AddHost(hosts, _settings.LastHost);
        AddHost(hosts, _lastUserHost);
        AddHost(hosts, _host);

        if (hosts.Count == 0)
        {
            hosts.Add("r743");
        }

        _hostOptions = hosts;
        OnPropertyChanged(nameof(HostOptions));

        if (!RemoteCommandsStore.IsValidHost(_host) ||
            !hosts.Contains(_host, StringComparer.OrdinalIgnoreCase))
        {
            SetHostValue(FirstValidHost(_lastUserHost, _settings.LastHost, _settings.DefaultHost, hosts[0]));
        }
    }

    private void OnSelectedCommandChanged()
    {
        OnPropertyChanged(nameof(SelectedCommand));
        OnPropertyChanged(nameof(UsesRemoteHost));
        OnPropertyChanged(nameof(IsHostSelectionEnabled));
        OnPropertyChanged(nameof(HostSelectionHint));
        OnPropertyChanged(nameof(Input1Label));
        OnPropertyChanged(nameof(Input1Placeholder));
        OnPropertyChanged(nameof(Input2Label));
        OnPropertyChanged(nameof(Input2Placeholder));

        if (SelectedCommand is not null)
        {
            IsSecondInputVisible = SelectedCommand.ShowSecondInput || _settings.TwoPane;
        }

        ApplySelectedCommandHost();
    }

    private void ApplySelectedCommandHost()
    {
        if (RemoteCommandsStore.IsValidHost(SelectedCommand?.Host))
        {
            SetHostValue(SelectedCommand!.Host);
        }
        else if (UsesRemoteHost)
        {
            SetHostValue(FirstValidHost(
                _lastUserHost,
                _settings.LastHost,
                _settings.DefaultHost,
                _hostOptions.FirstOrDefault() ?? "r743"));
        }

        OnPropertyChanged(nameof(IsHostSelectionEnabled));
        OnPropertyChanged(nameof(HostSelectionHint));
    }

    private void SetHostValue(string host)
    {
        var normalized = host.Trim();
        if (!string.Equals(_host, normalized, StringComparison.Ordinal))
        {
            _host = normalized;
            OnPropertyChanged(nameof(Host));
        }
    }

    private int FindCommandIndex(Func<RemoteCommandDefinition, bool> predicate)
    {
        for (var index = 0; index < _commands.Count; index++)
        {
            if (predicate(_commands[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static string FirstValidHost(params string[] candidates)
    {
        return candidates.FirstOrDefault(RemoteCommandsStore.IsValidHost) ?? "r743";
    }

    private static void AddHost(List<string> hosts, string? host)
    {
        if (!RemoteCommandsStore.IsValidHost(host))
        {
            return;
        }

        var normalized = host!.Trim();
        if (!hosts.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(normalized);
        }
    }
}
