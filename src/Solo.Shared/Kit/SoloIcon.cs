using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>
/// An icon, by name: the names a manifest's <c>nav</c> entry takes for its
/// <c>icon</c>. A name the client doesn't know is drawn as a neutral icon,
/// never as a gap.
/// </summary>
public sealed class SoloIcon : SoloKitComponent
{
    [Parameter, EditorRequired]
    public string Name { get; set; } = "";

    [Parameter]
    public SoloTone Tone { get; set; } = SoloTone.Default;

    /// <summary>What the icon means, for someone who can't see it. Leave it out when the text beside it already says.</summary>
    [Parameter]
    public string? Label { get; set; }
}
