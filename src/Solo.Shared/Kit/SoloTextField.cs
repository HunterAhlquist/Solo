using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>
/// Somewhere to write: a line of text, or several. Binds like an input:
/// <c>@bind-Value</c>.
/// </summary>
/// <remarks>
/// <see cref="ValueChanged"/> is raised as the person writes, each time they
/// pause, and when they leave the field — not once per key. The value is
/// yours: what the field shows once they have left it is whatever
/// <see cref="Value"/> you hand back, so a handler may tidy or refuse what was
/// written. There is no kind of field for a number or a date; what was written
/// arrives as text, and saying what is wrong with it is the extension's.
/// </remarks>
public sealed class SoloTextField : SoloKitComponent
{
    /// <summary>What is being asked for, as words with the field.</summary>
    [Parameter, EditorRequired]
    public string Label { get; set; } = "";

    [Parameter]
    public string Value { get; set; } = "";

    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    /// <summary>Shown in the field while it is empty: an example of what goes there.</summary>
    [Parameter]
    public string? Placeholder { get; set; }

    /// <summary>Room for several lines, where return starts a new one.</summary>
    [Parameter]
    public bool Multiline { get; set; }

    [Parameter]
    public bool Disabled { get; set; }
}
