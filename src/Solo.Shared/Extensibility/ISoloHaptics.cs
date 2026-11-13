namespace Solo.Shared.Extensibility;

/// <summary>
/// Haptic feedback on the device, by name: a tap as something is picked up, a
/// bump as it lands. The iPhone app plays it; a browser, the desktop app and
/// a device without haptics (every iPad) play nothing.
/// </summary>
/// <remarks>
/// <para>There are only the <see cref="SoloHaptics"/> names, and no way to
/// describe a pattern of your own: every installed extension's code can reach
/// this, so what each name feels like is the app shell's to decide, and an
/// extension never gets more of the device than one of those few names. It is
/// the reverse of <see cref="ISoloDeviceActions"/>, which hands the page a
/// name from the device.</para>
///
/// <para>Code that runs in the page's JavaScript can call
/// <c>window.solo.haptics.play(name)</c> with the same names instead. A host
/// older than contract 1.18 has no <c>window.solo.haptics</c>, so call it as
/// <c>window.solo?.haptics?.play(name)</c>, which does nothing there.</para>
///
/// <para>Inject this into a client page; a client half can't register
/// services of its own.</para>
/// </remarks>
public interface ISoloHaptics
{
    /// <summary>
    /// Plays <paramref name="haptic"/>, one of the <see cref="SoloHaptics"/>
    /// names, if the device can. Returns at once; an unknown name plays nothing.
    /// </summary>
    void Play(string haptic);

    /// <summary>
    /// Readies the device for a haptic that is likely to follow soon, such as
    /// when a press may become a drag, so the first one plays without delay.
    /// Optional: <see cref="Play"/> works without it, a beat later.
    /// </summary>
    void Prepare();
}

/// <summary>The names <see cref="ISoloHaptics"/> plays.</summary>
public static class SoloHaptics
{
    /// <summary>A light tap: something was picked up, such as a card a drag has taken hold of.</summary>
    public const string PickUp = "pick-up";

    /// <summary>A firmer bump: something was put down, such as a card dropped in a column.</summary>
    public const string Drop = "drop";

    /// <summary>A heavy bang that rumbles away for about two-thirds of a second, for something destroyed with a flourish.</summary>
    public const string Explosion = "explosion";
}
