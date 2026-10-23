using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Client;

/// <summary>
/// Shows a doc link's current note name (and heading) wherever core renders
/// markdown. A note the reader can't open, or that's gone, keeps the label it
/// was written with, which is all the server answers for it.
/// </summary>
[MarkdownLinkProvider(Order = 20)]
public sealed class NoteLinkProvider(ISoloClientApi client) : ISoloMarkdownLinkProvider
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, (string? Label, DateTimeOffset At)> _cache = [];

    public bool Claims(string url) => url.StartsWith("/docs/note/", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = urls.Distinct().Where(u => !_cache.TryGetValue(u, out var hit) || now - hit.At > Fresh).Take(200).ToList();
        if (stale.Count > 0)
        {
            var answered = await new DocsApi(client).LabelsAsync(stale, ct);
            foreach (var url in stale)
            {
                _cache[url] = (answered.GetValueOrDefault(url), now);
            }
        }

        var labels = new Dictionary<string, string>();
        foreach (var url in urls)
        {
            if (_cache.TryGetValue(url, out var hit) && hit.Label is { } label)
            {
                labels[url] = label;
            }
        }

        return labels;
    }
}
