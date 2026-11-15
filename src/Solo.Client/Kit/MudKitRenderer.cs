using Microsoft.AspNetCore.Components.Rendering;
using Solo.Shared.Kit;

namespace Solo.Client.Kit;

/// <summary>
/// The web client's drawing of the kit (<see cref="Solo.Shared.Kit"/>): each
/// kit component becomes the MudBlazor component the rest of the app would
/// have used in its place, so a contribution built from the kit looks like
/// core. The drawing itself is <see cref="KitView"/>, a Razor file; this only
/// hands the component to it.
/// </summary>
public sealed class MudKitRenderer : ISoloKitRenderer
{
    public void Render(RenderTreeBuilder builder, SoloKitComponent component)
    {
        builder.OpenComponent<KitView>(0);
        // The kit component itself, read afresh on every render: a reference
        // parameter is always taken as changed, so the view follows its owner.
        builder.AddComponentParameter(1, nameof(KitView.Source), component);
        builder.CloseComponent();
    }
}
