using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>
/// One line of a list: an icon, a title, and whatever small thing follows it —
/// a badge, a count. Pressable when it has an <see cref="OnClick"/> or an
/// <see cref="Href"/>; with both, the click is handled and then the client goes.
/// </summary>
public sealed class SoloRow : SoloKitComponent
{
    [Parameter, EditorRequired]
    public string Title { get; set; } = "";

    /// <summary>A second, quieter line under the title.</summary>
    [Parameter]
    public string? Subtitle { get; set; }

    /// <summary>An icon before the title, by the names <see cref="SoloIcon"/> takes.</summary>
    [Parameter]
    public string? Icon { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    /// <summary>A page of the app to open, relative to the instance, as <see cref="SoloButton.Href"/>.</summary>
    [Parameter]
    public string? Href { get; set; }

    /// <summary>What follows the title, at the row's far end. Kit components.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
