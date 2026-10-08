namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered in the app bar, before the connection indicator
/// and the account menu, on every page once signed in (contract 1.8).
/// Components receive no parameters. Each has an error boundary; only loaded
/// extension assemblies can contribute.
/// </summary>
/// <remarks>
/// <para>The one place that is always on screen, which makes it the place for
/// something that changes while the person is elsewhere: an unread count, a
/// running timer. Keep it to an icon button with a badge — the bar is narrow on
/// a phone, and every extension shares it. A manifest nav entry is still the
/// way to a page; this is for state.</para>
///
/// <para>For a count that follows the person rather than a project, publish
/// with <c>ISoloLiveUpdates.PublishToUserAsync</c> and listen with
/// <see cref="ISoloLiveTopics.SubscribeUserAsync"/>.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AppBarExtensionAttribute : Attribute
{
    /// <summary>Ascending among contributions, left to right; ties break by extension id.</summary>
    public int Order { get; set; }
}
