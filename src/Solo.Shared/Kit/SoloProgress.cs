using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>How far along something is.</summary>
public sealed class SoloProgress : SoloKitComponent
{
    /// <summary>From 0, not started, to 1, done. Anything outside is drawn as the nearer end.</summary>
    [Parameter, EditorRequired]
    public double Value { get; set; }

    [Parameter]
    public SoloTone Tone { get; set; } = SoloTone.Primary;

    /// <summary>What is progressing, for someone who can't see the bar: "3 of 8 tasks done".</summary>
    [Parameter]
    public string? Label { get; set; }
}
