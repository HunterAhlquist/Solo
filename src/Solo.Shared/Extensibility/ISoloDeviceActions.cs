namespace Solo.Shared.Extensibility;

/// <summary>
/// Actions the device itself asks for, such as an Apple Pencil double-tap in
/// the iPad app, delivered by name. A browser has no such controls, so there
/// a handler simply never runs.
/// </summary>
/// <remarks>
/// <para>The app shell turns the gesture into one of the <see cref="DeviceActions"/>
/// names and the page decides what it means. An extension never touches the
/// native side, so the same name can come from another platform's control
/// later without the extension changing.</para>
///
/// <para>Inject this into a client page; a client half can't register
/// services of its own.</para>
/// </remarks>
public interface ISoloDeviceActions
{
    /// <summary>
    /// Runs <paramref name="handler"/> whenever the device asks for
    /// <paramref name="actionId"/>, until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// Only the newest live handler for a name runs, so a dialog opened over a
    /// page takes the action while it is open and the page gets it back when
    /// it closes.
    /// </remarks>
    IDisposable Handle(string actionId, Func<Task> handler);
}

/// <summary>The action names <see cref="ISoloDeviceActions"/> delivers.</summary>
public static class DeviceActions
{
    /// <summary>
    /// The stylus's own tool-swap control: on iPad, an Apple Pencil double-tap
    /// when the person's Pencil setting is to switch to the eraser or to the
    /// previous tool.
    /// </summary>
    public const string StylusToolSwap = "action_stylus-tool-swap";
}
