using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>A setting that is on or off. Binds like a checkbox: <c>@bind-Value</c>.</summary>
public sealed class SoloToggle : SoloKitComponent
{
    /// <summary>What is switched, as words beside the switch.</summary>
    [Parameter, EditorRequired]
    public string Label { get; set; } = "";

    [Parameter]
    public bool Value { get; set; }

    [Parameter]
    public EventCallback<bool> ValueChanged { get; set; }

    [Parameter]
    public bool Disabled { get; set; }
}
