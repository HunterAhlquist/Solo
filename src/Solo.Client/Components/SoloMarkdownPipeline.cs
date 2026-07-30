using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Solo.Client.Components;

/// <summary>
/// The Markdig pipeline every rendered description and comment goes through.
///
/// MudMarkdown's default pipeline passes raw HTML straight into the DOM and
/// hands link/image destinations to the browser untouched — so a task
/// description or comment containing <c>&lt;img src=x onerror=…&gt;</c>, or a
/// <c>[click](javascript:…)</c> link, executes script in whoever opens the
/// board. That is stored XSS on the app's own origin, where the session token
/// lives, and comments only need read access to post. Both holes are closed
/// here rather than at the call sites: <see cref="SoloMarkdown"/> is the only
/// component that renders user markdown, and it always uses this pipeline.
/// </summary>
public static class SoloMarkdownPipeline
{
    /// <summary>
    /// Schemes a link or image may point at. Anything else — javascript:,
    /// data:, vbscript:, file:, and the rest — is neutralised. Relative and
    /// fragment URLs carry no scheme and are always allowed; that is what
    /// Solo's own ||SERIAL|| task links resolve to.
    /// </summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>Where a rejected destination points instead: nowhere.</summary>
    private const string NeutralisedUrl = "about:blank#unsafe-link-removed";

    public static MarkdownPipeline Instance { get; } = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        // Drops the raw-HTML block and inline parsers, so markup in user text
        // renders as the literal characters the author typed.
        .DisableHtml()
        .Use<SafeUriExtension>()
        .Build();

    /// <summary>
    /// True when a link or image destination is safe to hand to the browser:
    /// relative, or one of <see cref="AllowedSchemes"/>. Public so the tests
    /// can pin the rule down directly.
    /// </summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var trimmed = url.Trim();

        // A scheme is everything before the first ':', but only when no '/',
        // '?' or '#' comes first — "/a:b" and "a/b:c" are relative paths.
        var colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        var separator = trimmed.AsSpan(0, colon).IndexOfAny('/', '?', '#');
        if (separator >= 0)
        {
            return true;
        }

        var scheme = trimmed[..colon];
        return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rewrites unsafe destinations once the document is parsed, which catches
    /// every syntax that produces one — inline links, reference links, images
    /// and autolinks alike — without the pipeline having to guess at the raw
    /// markdown text.
    /// </summary>
    private sealed class SafeUriExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            pipeline.DocumentProcessed -= Sanitise;
            pipeline.DocumentProcessed += Sanitise;
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
        }

        private static void Sanitise(MarkdownDocument document)
        {
            foreach (var node in document.Descendants())
            {
                switch (node)
                {
                    case LinkInline link when !IsSafeUrl(link.Url):
                        link.Url = NeutralisedUrl;
                        break;

                    case AutolinkInline autolink when !IsSafeUrl(autolink.Url):
                        autolink.Url = NeutralisedUrl;
                        break;
                }
            }

            foreach (var definition in document.Descendants<LinkReferenceDefinitionGroup>()
                         .SelectMany(group => group.Descendants<LinkReferenceDefinition>()))
            {
                if (!IsSafeUrl(definition.Url))
                {
                    definition.Url = NeutralisedUrl;
                }
            }
        }
    }
}
