using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>A piece of text. Plain text: what it says, not markup.</summary>
public sealed class SoloText : SoloKitComponent
{
    [Parameter, EditorRequired]
    public string Text { get; set; } = "";

    [Parameter]
    public SoloTextVariant Variant { get; set; } = SoloTextVariant.Body;

    [Parameter]
    public SoloTone Tone { get; set; } = SoloTone.Default;
}
