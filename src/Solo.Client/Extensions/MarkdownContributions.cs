using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solo.Client.Components;
using Solo.Shared.Extensibility;

namespace Solo.Client.Extensions;

/// <summary>Owns the contributed instances for one editor or rendered markdown component.</summary>
public sealed class MarkdownContributions(IServiceProvider services) : IAsyncDisposable
{
    public sealed record ActionButton(string Id, MarkdownEditorButton Button, ISoloMarkdownEditorAction Action);
    private readonly List<object> _instances = [];
    private readonly ILogger? _logger = services.GetService<ILoggerFactory>()?.CreateLogger<MarkdownContributions>();
    private IReadOnlyList<ISoloMarkdownEditorAction>? _actions;
    private IReadOnlyList<ISoloMarkdownLinkProvider>? _links;

    private IReadOnlyList<T> Create<TAttribute, T>(Func<TAttribute, int> order) where TAttribute : Attribute
    {
        if (services.GetService<ExtensionCatalog>() is not { } catalog) return [];
        var result = new List<T>();
        foreach (var type in ContributedSections.Implementations<TAttribute, T>(catalog, order))
        {
            try
            {
                var instance = ActivatorUtilities.CreateInstance(services, type);
                _instances.Add(instance);
                result.Add((T)instance);
            }
            catch (Exception e) { _logger?.LogWarning(e, "Couldn't create markdown contribution {Type}.", type); }
        }
        return result;
    }

    public async Task<IReadOnlyList<ActionButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct)
    {
        _actions ??= Create<MarkdownEditorActionAttribute, ISoloMarkdownEditorAction>(a => a.Order);
        var buttons = new List<ActionButton>();
        for (var i = 0; i < _actions.Count; i++)
        {
            var action = _actions[i];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var offered = await action.ButtonsAsync(context, timeout.Token).AsTask().WaitAsync(timeout.Token);
                buttons.AddRange(offered.Select(b => new ActionButton($"{i}:{b.Id}", b, action)));
            }
            catch (Exception e) { _logger?.LogWarning(e, "Markdown action {Type} failed or was too slow.", action.GetType()); }
        }
        return buttons;
    }

    public async Task<string> LabelsAsync(string markdown, CancellationToken ct)
    {
        _links ??= Create<MarkdownLinkProviderAttribute, ISoloMarkdownLinkProvider>(a => a.Order);
        if (_links.Count == 0) return markdown;
        var document = Markdown.Parse(markdown, SoloMarkdownPipeline.Instance);
        var pending = document.Descendants<LinkInline>()
            .Where(l => !l.IsImage && l.Url is not null && SoloMarkdownPipeline.IsSafeUrl(l.Url) &&
                        l.LabelSpan.Start > l.Span.Start && l.LabelSpan.End < markdown.Length && l.LabelSpan.Length > 0)
            .ToList();
        var replacements = new List<(int Start, int Length, string Label)>();
        foreach (var provider in _links)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var claimed = pending.Where(l => provider.Claims(l.Url!)).ToList();
                pending.RemoveAll(claimed.Contains);
                if (claimed.Count == 0) continue;
                var labels = await provider.LabelsAsync(claimed.Select(l => l.Url!).Distinct().ToList(), timeout.Token)
                    .AsTask().WaitAsync(timeout.Token);
                foreach (var link in claimed)
                    if (labels.TryGetValue(link.Url!, out var label))
                        replacements.Add((link.LabelSpan.Start, link.LabelSpan.Length, EscapeLabel(label)));
            }
            catch (Exception e) { _logger?.LogWarning(e, "Markdown link provider {Type} failed or was too slow.", provider.GetType()); }
        }
        foreach (var (start, length, label) in replacements.OrderByDescending(r => r.Start))
            markdown = markdown.Remove(start, length).Insert(start, label);
        return markdown;
    }

    // Labels are text, never a second markdown input or an HTML escape hatch.
    private static string EscapeLabel(string label) => string.Concat(label.Select(c =>
        char.IsAscii(c) && char.IsPunctuation(c) || c is '<' or '>' or '`' or '~' or '+' or '=' or '|' or '^'
            ? "\\" + c : char.IsControl(c) ? " " : c.ToString()));

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _instances)
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable: await asyncDisposable.DisposeAsync(); break;
                case IDisposable disposable: disposable.Dispose(); break;
            }
        _instances.Clear();
    }
}
