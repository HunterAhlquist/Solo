using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>
/// Something to press. <see cref="OnClick"/> for something to do,
/// <see cref="Href"/> for somewhere to go; with both, the click is handled and
/// then the client goes.
/// </summary>
public sealed class SoloButton : SoloKitComponent
{
    [Parameter, EditorRequired]
    public string Text { get; set; } = "";

    /// <summary>An icon before the text, by the names <see cref="SoloIcon"/> takes.</summary>
    [Parameter]
    public string? Icon { get; set; }

    [Parameter]
    public SoloButtonVariant Variant { get; set; } = SoloButtonVariant.Secondary;

    /// <summary>The action deletes or can't be undone, and should look it.</summary>
    [Parameter]
    public bool Destructive { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    /// <summary>
    /// A page of the app to open, relative to the instance:
    /// <c>projects/DMD/boards/main</c>. The client opens it as it opens its own
    /// links, which for one that is not a browser may mean a screen of its own.
    /// </summary>
    [Parameter]
    public string? Href { get; set; }
}
