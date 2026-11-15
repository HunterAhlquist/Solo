using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>
/// Lays kit components out in a line, down or across. The one kit component
/// that is only arrangement; what goes in it should be kit components too,
/// since a client that draws the kit itself shows nothing else.
/// <para>A stack that runs down takes the width of the place it is in, and
/// <see cref="Align"/> says where its children sit across that width. One that
/// runs across is as wide as its children.</para>
/// </summary>
public sealed class SoloStack : SoloKitComponent
{
    [Parameter]
    public SoloDirection Direction { get; set; } = SoloDirection.Vertical;

    [Parameter]
    public SoloSpacing Spacing { get; set; } = SoloSpacing.Small;

    /// <summary>Across the direction: for a row, how its children sit top to bottom.</summary>
    [Parameter]
    public SoloAlign Align { get; set; } = SoloAlign.Start;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
