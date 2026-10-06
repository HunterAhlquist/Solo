namespace Solo.Shared.Extensibility;

/// <summary>
/// The host's context menu: a short list of actions shown where the person
/// right-clicked. In the desktop app it is the operating system's own menu; in
/// a browser the host draws it.
/// </summary>
/// <remarks>
/// <para>Core shows it because only core can reach the desktop shell. An
/// extension drawing its own menu would look native nowhere, and a page can't
/// ask Electron for a real one without a bridge an extension has no business
/// calling directly. Inject this into a client page; a client half can't
/// register services of its own.</para>
///
/// <para>Showing a menu is all it does. Whatever the caller does with the
/// choice still goes through its own endpoint's checks, so leave out what the
/// person can't do rather than relying on the menu to stop them.</para>
/// </remarks>
public interface ISoloContextMenu
{
    /// <summary>
    /// Shows <paramref name="items"/> at a point in the viewport, usually the
    /// pointer event's <c>clientX</c>/<c>clientY</c>, and waits for a choice.
    /// Returns the chosen item's <see cref="ContextMenuItem.Id"/>, or null if
    /// the person dismisses the menu, if <paramref name="items"/> is empty, or
    /// if <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// One menu is open at a time: showing another dismisses the first, whose
    /// call then returns null. Navigating away dismisses it too. The desktop
    /// app opens its menu at the pointer, wherever the point says.
    /// </remarks>
    Task<string?> ShowAsync(
        double clientX, double clientY, IReadOnlyList<ContextMenuItem> items, CancellationToken ct = default);
}

/// <summary>One action in a <see cref="ISoloContextMenu"/>.</summary>
/// <param name="Id">What <see cref="ISoloContextMenu.ShowAsync"/> returns when this is chosen.</param>
/// <param name="Label">The text shown, and all a native menu shows, so it has to say the whole action.</param>
public sealed record ContextMenuItem(string Id, string Label)
{
    /// <summary>
    /// A MudBlazor icon (an <c>Icons.Material…</c> value) drawn beside the
    /// label in a browser. Native menus in the desktop app leave it out.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Drawn in the theme's error colour in a browser, for actions that take
    /// something away. Native menus have no such colour, so the label still has
    /// to make that plain on its own.
    /// </summary>
    public bool Danger { get; init; }

    /// <summary>Shown greyed out and can't be chosen.</summary>
    public bool Disabled { get; init; }

    /// <summary>Starts a new group: a divider goes above this item, unless it's the first.</summary>
    public bool SeparatorBefore { get; init; }
}
