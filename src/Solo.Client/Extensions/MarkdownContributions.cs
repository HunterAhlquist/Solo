using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solo.Client.Components;
using Solo.Shared.Extensibility;

namespace Solo.Client.Extensions;

/// <summary>
/// What extensions add to core's markdown: buttons in the editor
/// (<see cref="ISoloMarkdownEditorAction"/>) and labels for the links they own
/// (<see cref="ISoloMarkdownLinkProvider"/>).
/// </summary>
/// <remarks>
/// <para>Both are classes in loaded extension assemblies, found by attribute and
/// created from this scope's services, as <c>TaskMenuService</c> creates menu
/// contributors. Editor actions are created per editor and disposed with it;
/// link providers once per session, so their caches last.</para>
///
/// <para>An extension that throws or is slow costs only its own part: its
/// buttons or labels are left out, never the editor or the text.</para>
/// </remarks>
public sealed class MarkdownContributions(
    ExtensionCatalog catalog,
    IServiceProvider services,
    ILogger<MarkdownContributions> logger) : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The renderer's own pipeline plus source positions, which locate each
    /// link's label in the text so it can be replaced in place.
    /// </summary>
    private static readonly MarkdownPipeline LocatingPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .UsePreciseSourceLocation()
        .Build();

    private IReadOnlyList<ISoloMarkdownLinkProvider>? _providers;
    private int _providersFor = -1;
    private IReadOnlyList<Type>? _actionTypes;
    private int _actionTypesFor = -1;

    // -- Editor actions -------------------------------------------------------

    /// <summary>The action types loaded extensions contribute, in order. Cheap after the first call.</summary>
    public IReadOnlyList<Type> ActionTypes
    {
        get
        {
            if (_actionTypes is null || _actionTypesFor != catalog.Loaded.Count)
            {
                _actionTypes = ContributedSections
                    .Implementations<MarkdownEditorActionAttribute, ISoloMarkdownEditorAction>(catalog, a => a.Order);
                _actionTypesFor = catalog.Loaded.Count;
            }

            return _actionTypes;
        }
    }

    /// <summary>One editor's actions, created for it. Dispose them with <see cref="DisposeAllAsync"/> when it closes.</summary>
    public IReadOnlyList<ISoloMarkdownEditorAction> CreateActions() =>
        ActionTypes.Select(type => Create<ISoloMarkdownEditorAction>(type)).OfType<ISoloMarkdownEditorAction>().ToList();

    /// <summary>
    /// The buttons every action offers this editor, each id prefixed with its
    /// action's position so two extensions can both say <c>"link"</c>.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, MarkdownEditorButton Button, ISoloMarkdownEditorAction Action)>>
        ButtonsAsync(IReadOnlyList<ISoloMarkdownEditorAction> actions, MarkdownEditorContext context)
    {
        var buttons = new List<(string, MarkdownEditorButton, ISoloMarkdownEditorAction)>();
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            using var timeout = new CancellationTokenSource(Timeout);
            try
            {
                var offered = await action.ButtonsAsync(context, timeout.Token).AsTask().WaitAsync(Timeout);
                buttons.AddRange(offered.Select(b => ($"{index}:{b.Id}", b, action)));
            }
            catch (Exception e)
            {
                logger.LogError(e, "Markdown editor action {Type} offered nothing: it failed or was too slow.",
                    action.GetType().FullName);
            }
        }

        return buttons;
    }

    public static async ValueTask DisposeAllAsync(IEnumerable<object> instances)
    {
        foreach (var instance in instances)
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    // -- Link labels ----------------------------------------------------------

    private IReadOnlyList<ISoloMarkdownLinkProvider> Providers
    {
        get
        {
            // The catalog only grows, once, after sign-in; its count is enough to notice.
            if (_providers is null || _providersFor != catalog.Loaded.Count)
            {
                _providers = ContributedSections
                    .Implementations<MarkdownLinkProviderAttribute, ISoloMarkdownLinkProvider>(catalog, a => a.Order)
                    .Select(type => Create<ISoloMarkdownLinkProvider>(type))
                    .OfType<ISoloMarkdownLinkProvider>()
                    .ToList();
                _providersFor = catalog.Loaded.Count;
            }

            return _providers;
        }
    }

    public bool HasLinkProviders => Providers.Count > 0;

    /// <summary>
    /// <paramref name="markdown"/> with every claimed link's label replaced by
    /// its provider's answer, or the same string when nothing was claimed or
    /// answered.
    /// </summary>
    public async Task<string> RelabelAsync(string markdown, CancellationToken ct = default)
    {
        var providers = Providers;
        if (providers.Count == 0 || !markdown.Contains("]("))
        {
            return markdown;
        }

        var links = ClaimableLinks(markdown);
        if (links.Count == 0)
        {
            return markdown;
        }

        // The first provider to claim a URL answers for it.
        var byProvider = links.Select(l => l.Url).Distinct()
            .Select(url => (Url: url, Provider: providers.FirstOrDefault(p => SafeClaims(p, url))))
            .Where(c => c.Provider is not null)
            .GroupBy(c => c.Provider!, c => c.Url);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in byProvider)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var answered = await group.Key.LabelsAsync(group.ToList(), timeout.Token).AsTask().WaitAsync(Timeout, ct);
                foreach (var url in group)
                {
                    if (answered.TryGetValue(url, out var label) && !string.IsNullOrWhiteSpace(label))
                    {
                        labels[url] = label;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(e, "Markdown link provider {Type} failed or was too slow; its links keep their labels.",
                    group.Key.GetType().FullName);
            }
        }

        return labels.Count == 0 ? markdown : Replace(markdown, links, labels);
    }

    /// <summary>
    /// Ordinary links, with a label and a destination core would render as-is.
    /// Public so the tests can pin down what is offered.
    /// </summary>
    public static IReadOnlyList<(string Url, int LabelStart, int LabelEnd)> ClaimableLinks(string markdown)
    {
        var document = Markdown.Parse(markdown, LocatingPipeline);
        var links = new List<(string, int, int)>();
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage || link.Url is not { Length: > 0 } url || !SoloMarkdownPipeline.IsSafeUrl(url))
            {
                continue;
            }

            // The label is what the link's children cover. LabelSpan would do
            // for an inline link, but on a reference link it points at the
            // reference's name, and replacing that would break the link.
            if (link.FirstChild is not { } first || link.LastChild is not { } last)
            {
                continue;
            }

            var (start, end) = (first.Span.Start, last.Span.End);
            if (start < 0 || end >= markdown.Length || end < start)
            {
                continue;
            }

            links.Add((url, start, end));
        }

        return links;
    }

    private static string Replace(
        string markdown, IReadOnlyList<(string Url, int LabelStart, int LabelEnd)> links,
        IReadOnlyDictionary<string, string> labels)
    {
        var builder = new StringBuilder(markdown);
        // From the end backwards, so earlier offsets stay where they were.
        foreach (var (url, start, end) in links.OrderByDescending(l => l.LabelStart))
        {
            if (labels.TryGetValue(url, out var label))
            {
                builder.Remove(start, end - start + 1).Insert(start, Escape(label));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Backslash-escapes every ASCII punctuation character, which CommonMark
    /// allows for all of them: a label is shown as the text it says, never as markup.
    /// Line breaks become spaces, since a label can't span lines.
    /// </summary>
    public static string Escape(string label)
    {
        var builder = new StringBuilder(label.Length + 8);
        foreach (var c in label)
        {
            if (c is '\r' or '\n')
            {
                builder.Append(' ');
            }
            else if (char.IsAsciiLetterOrDigit(c) || c == ' ' || c > 127)
            {
                builder.Append(c);
            }
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                builder.Append('\\').Append(c);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private bool SafeClaims(ISoloMarkdownLinkProvider provider, string url)
    {
        try
        {
            return provider.Claims(url);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Markdown link provider {Type} failed to answer Claims.", provider.GetType().FullName);
            return false;
        }
    }

    private T? Create<T>(Type type) where T : class
    {
        try
        {
            return (T)ActivatorUtilities.CreateInstance(services, type);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Couldn't create {Contract} {Type}.", typeof(T).Name, type.FullName);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_providers is not null)
        {
            await DisposeAllAsync(_providers);
        }
    }
}
