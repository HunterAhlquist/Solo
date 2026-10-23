namespace Solo.Shared.Extensibility;

/// <summary>
/// Supplies the label for links an extension owns wherever core renders
/// markdown — task and epic descriptions and comments (contract 1.10). A link to
/// one of the extension's records can then show that record's current name,
/// however long ago the link was written.
/// </summary>
/// <remarks>
/// <para>A class, found by <see cref="MarkdownLinkProviderAttribute"/> and
/// created from the host's services, like <see cref="ISoloMarkdownEditorAction"/>.
/// One instance serves the signed-in session, so it may cache.</para>
///
/// <para>Core renders the markdown as written straight away, then asks each
/// provider once per render for the links it <see cref="Claims"/>, and renders
/// again with the answers. Caching is the provider's: core asks on every render.
/// A provider that throws or takes longer than a second leaves its links as
/// written. So does an uninstalled extension, which is what keeps the links
/// readable without it.</para>
///
/// <para>Only ordinary links are offered — not images, and not links core's
/// renderer would neutralise as unsafe. The label is replaced as plain text;
/// it can't add markup.</para>
/// </remarks>
public interface ISoloMarkdownLinkProvider
{
    /// <summary>Cheap and synchronous: does this provider own the link? No I/O.</summary>
    bool Claims(string url);

    /// <summary>
    /// Labels for the claimed links, keyed by URL. A URL left out keeps the
    /// label it was written with — which is also the answer for a record the
    /// reader can't see, so that its existence isn't revealed.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyList<string> urls, CancellationToken ct);
}

/// <summary>Marks an <see cref="ISoloMarkdownLinkProvider"/> for the host to find.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkdownLinkProviderAttribute : Attribute
{
    /// <summary>Ascending among providers; the first to claim a link answers for it.</summary>
    public int Order { get; set; }
}
