using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;

namespace RemoteCommands.Surface.ViewModels;

public sealed class RemoteCommandInputViewModel : MptObservableViewModel
{
    private string _value;

    public RemoteCommandInputViewModel(RemoteCommandInputDefinition definition, string value)
    {
        Definition = definition;
        _value = value;
    }

    public RemoteCommandInputDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Label => Definition.Required ? Definition.Label + " *" : Definition.Label;

    public string Placeholder => Definition.Placeholder;

    public string Description => Definition.Description;

    public bool IsMultiline => string.Equals(
        Definition.Kind,
        "multiline",
        StringComparison.OrdinalIgnoreCase);

    public double MinimumHeight => IsMultiline ? 112 : 36;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? "");
    }
}
