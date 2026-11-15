using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>A word or two in a pill: a state, a count, a tag. Something to read at a glance, not to press.</summary>
public sealed class SoloBadge : SoloKitComponent
{
    [Parameter, EditorRequired]
    public string Text { get; set; } = "";

    /// <summary>An icon before the text, by the names <see cref="SoloIcon"/> takes.</summary>
    [Parameter]
    public string? Icon { get; set; }

    [Parameter]
    public SoloTone Tone { get; set; } = SoloTone.Default;
}
