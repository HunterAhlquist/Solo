using Microsoft.AspNetCore.Components;

namespace Solo.Shared.Kit;

/// <summary>One of the things a <see cref="SoloPicker"/> offers.</summary>
/// <param name="Value">What <see cref="SoloPicker.ValueChanged"/> is handed when this is picked. Not shown.</param>
/// <param name="Label">What the person reads.</param>
public sealed record SoloOption(string Value, string Label);

/// <summary>
/// One choice out of a few. Binds like a select: <c>@bind-Value</c>.
/// </summary>
/// <remarks>
/// For a handful of options that fit in a menu. A <see cref="Value"/> that is
/// none of the <see cref="Options"/> is nothing picked, and the picker shows
/// its <see cref="Placeholder"/>; only an option's value is ever handed back.
/// </remarks>
public sealed class SoloPicker : SoloKitComponent
{
    /// <summary>What is being chosen, as words with the picker.</summary>
    [Parameter, EditorRequired]
    public string Label { get; set; } = "";

    /// <summary>The <see cref="SoloOption.Value"/> of the option that is picked.</summary>
    [Parameter]
    public string? Value { get; set; }

    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<SoloOption> Options { get; set; } = [];

    /// <summary>Shown while nothing is picked.</summary>
    [Parameter]
    public string? Placeholder { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>
    /// For whatever draws the picker: raises <see cref="ValueChanged"/> with
    /// what was chosen, if it is one of the <see cref="Options"/> and the
    /// picker is not disabled. A renderer is taken at its word about which
    /// option, never about what the options are.
    /// </summary>
    public Task PickAsync(string? value) =>
        value is not null && !Disabled && Options.Any(option => option.Value == value)
            ? ValueChanged.InvokeAsync(value)
            : Task.CompletedTask;
}
