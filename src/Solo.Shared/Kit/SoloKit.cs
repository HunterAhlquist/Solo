using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Solo.Shared.Kit;

// The kit: a small set of components an extension builds a contribution from
// when it wants that contribution to be more than markup. A kit component says
// what something is — a row, a badge, a switch — and never how it is drawn.
// The host draws it: Solo's web client with the components the rest of the app
// is made of, and a client that is not a browser with controls of its own.
//
// That is the whole point, and it is why the kit is closed and plain. There is
// no CSS to pass, no markup inside a piece of text, and nothing in it that
// belongs to one platform: whatever a kit component can say, every client can
// draw. Anything richer is ordinary Razor, which the web shows as it always has.

/// <summary>How much a piece of the interface asks to be noticed, and in what sense.</summary>
public enum SoloTone
{
    /// <summary>As the text around it.</summary>
    Default,

    /// <summary>Quieter than the text around it: a note, a count, something secondary.</summary>
    Muted,

    /// <summary>The instance theme's own colour.</summary>
    Primary,
    Success,
    Warning,
    Danger,
}

/// <summary>What a piece of text is for.</summary>
public enum SoloTextVariant
{
    Body,

    /// <summary>Small print under or beside something.</summary>
    Caption,

    /// <summary>The name of a value shown next to it.</summary>
    Label,

    /// <summary>The title of a group of things.</summary>
    Heading,
}

/// <summary>Which way a <see cref="SoloStack"/> lays its children out.</summary>
public enum SoloDirection
{
    Vertical,
    Horizontal,
}

/// <summary>The room between the children of a <see cref="SoloStack"/>.</summary>
public enum SoloSpacing
{
    None,
    Small,
    Medium,
    Large,
}

/// <summary>Where the children of a <see cref="SoloStack"/> sit across its direction.</summary>
public enum SoloAlign
{
    Start,
    Center,
    End,
    Stretch,
}

/// <summary>How much weight a <see cref="SoloButton"/> carries among the buttons beside it.</summary>
public enum SoloButtonVariant
{
    /// <summary>The one thing this group of buttons is for.</summary>
    Primary,
    Secondary,

    /// <summary>No more than its words: for the least of several actions.</summary>
    Plain,
}

/// <summary>
/// Draws kit components. The host registers one; a kit component asks for it
/// and hands itself over, parameters and all. Without one — in a test host,
/// say — kit components draw themselves as plain HTML.
/// </summary>
public interface ISoloKitRenderer
{
    /// <summary>
    /// Writes what <paramref name="component"/> should look like. Called from
    /// the component's own render, so anything written belongs to it.
    /// </summary>
    void Render(RenderTreeBuilder builder, SoloKitComponent component);
}

/// <summary>
/// What every kit component is. The set is closed: the components are the ones
/// in this namespace, because each is a promise that every client can draw it.
/// </summary>
public abstract class SoloKitComponent : ComponentBase
{
    private ISoloKitRenderer? _renderer;

    private protected SoloKitComponent()
    {
    }

    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    protected sealed override void BuildRenderTree(RenderTreeBuilder builder)
    {
        _renderer ??= Services.GetService(typeof(ISoloKitRenderer)) as ISoloKitRenderer ?? PlainKitRenderer.Instance;
        _renderer.Render(builder, this);
    }
}
