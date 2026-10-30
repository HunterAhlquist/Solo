using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Solo.Api.Updates;

/// <summary>
/// Fetches and caches release manifests. One entry per URL, because the host's
/// own feed and each third-party extension's feed are separate documents.
/// </summary>
/// <remarks>
/// <para>The cache is what makes this safe to call from a page load. Without it
/// every admin visit would hit a third party, and an instance with several
/// third-party extensions would hit several — enough to run into anonymous
/// rate limits and slow the page while doing it.</para>
///
/// <para>A failure is cached too, for a shorter time. The alternative is that a
/// feed which is down gets re-requested on every single page load, which is the
/// worst thing to do to a service that is already struggling, and it makes the
/// admin page as slow as the broken feed.</para>
/// </remarks>
public sealed class ReleaseFeedClient(
    IHttpClientFactory factory,
    IOptions<UpdateOptions> options,
    TimeProvider time,
    ILogger<ReleaseFeedClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Long enough that a down feed is not hammered, short enough that fixing
    /// it doesn't mean waiting out the success cache.
    /// </summary>
    private static readonly TimeSpan FailureCacheFor = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly UpdateOptions _options = options.Value;

    public async Task<FeedResult> GetAsync(string url, bool force, CancellationToken ct)
    {
        // Serialised rather than per-URL locked: a handful of feeds, fetched at
        // most a few times a day, and an admin page load is the only caller.
        // Concurrent fetches of *different* feeds would be a micro-optimisation
        // bought with a second locking scheme to get wrong.
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (!force && _cache.TryGetValue(url, out var cached) && cached.ExpiresAt > now)
            {
                return cached.Result;
            }

            var result = await FetchAsync(url, ct);
            _cache[url] = new CacheEntry(
                result,
                now + (result.Feed is null ? FailureCacheFor : _options.CacheFor));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FeedResult> FetchAsync(string url, CancellationToken ct)
    {
        var fetchedAt = time.GetUtcNow();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            // Not a transient failure, so it is reported as a configuration
            // error rather than retried. http is refused even here, where only
            // a version number is read: the answer decides what gets installed.
            return new FeedResult(null, fetchedAt, $"'{url}' is not an https URL.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.Timeout);

            var client = factory.CreateClient(UpdateOptions.HttpClient);
            using var response = await client.GetAsync(uri, cts.Token);

            // A release with no manifest asset is a plain 404 through the
            // /releases/latest/download/ redirect. Worth its own sentence: it
            // means "nothing published a manifest", not "the network is down".
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new FeedResult(null, fetchedAt, "No release manifest was published at that URL.");
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var feed = await JsonSerializer.DeserializeAsync<ReleaseFeed>(stream, Json, cts.Token);

            if (feed is null)
            {
                return new FeedResult(null, fetchedAt, "The release manifest was empty.");
            }

            if (feed.Schema > ReleaseFeed.SupportedSchema)
            {
                // Forward compatibility has a floor. A schema bump means a field
                // changed meaning, so reading it with these rules could report
                // the wrong version — worse than reporting nothing.
                return new FeedResult(null, fetchedAt,
                    $"The release manifest uses schema {feed.Schema}, which this version of Solo " +
                    "does not understand. Update Solo to read it.");
            }

            return new FeedResult(feed, fetchedAt, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FeedResult(null, fetchedAt, "The update check timed out.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            // Logged at information: an instance with no outbound access would
            // otherwise fill its log with warnings about a feature it is not
            // using, and the reason is shown in the UI either way.
            logger.LogInformation(e, "Could not read the release manifest at {Url}.", url);
            return new FeedResult(null, fetchedAt, e.Message);
        }
    }

    private sealed record CacheEntry(FeedResult Result, DateTimeOffset ExpiresAt);
}

/// <param name="Feed">The manifest, or null when it could not be read.</param>
/// <param name="FetchedAt">When this answer was produced.</param>
/// <param name="Error">Why it could not be read, when it could not.</param>
public sealed record FeedResult(ReleaseFeed? Feed, DateTimeOffset FetchedAt, string? Error);
