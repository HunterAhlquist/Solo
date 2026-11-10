using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Solo.Api;

/// <summary>
/// The app's content security policy, completed with the identity provider's
/// endpoints once its discovery document has been read (see
/// <see cref="SecurityHeaders.ProviderConnectSources"/> and
/// <see cref="SecurityHeaders.ProviderFrameSources"/>).
/// </summary>
/// <remarks>
/// <para>The document comes through the bearer scheme's own configuration
/// manager, so it is the copy token validation already fetches and caches, at
/// the same address and under the same https rule.</para>
///
/// <para>Until it has been read, responses carry the policy with the
/// authority's origin alone — enough to fetch the document from a browser, not
/// to finish a sign-in whose userinfo endpoint lives elsewhere. The first
/// requests after a start wait a few seconds for it rather than serve that,
/// because the page they serve may be the sign-in callback. If it can't be
/// read, later requests don't wait: they get the partial policy while it is
/// retried in the background, so a provider outage doesn't slow every
/// response down.</para>
/// </remarks>
public sealed class ContentSecurityPolicies
{
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly string? _provider;
    private readonly string? _authority;
    private readonly Func<CancellationToken, Task<OpenIdConnectConfiguration>>? _discover;
    private readonly TimeProvider _time;
    private readonly ILogger<ContentSecurityPolicies> _logger;
    private readonly string _partial;
    private readonly Lock _gate = new();
    private string? _complete;
    private Task<string?>? _attempt;
    private DateTimeOffset _retryAt;
    private bool _failed;

    public ContentSecurityPolicies(
        string? provider,
        string? authority,
        Func<CancellationToken, Task<OpenIdConnectConfiguration>>? discover,
        TimeProvider time,
        ILogger<ContentSecurityPolicies> logger)
    {
        _provider = provider;
        _authority = authority;
        _discover = discover;
        _time = time;
        _logger = logger;
        _partial = SecurityHeaders.ContentSecurityPolicyFor(provider, authority);

        // Google, an unconfigured instance, or nothing to read the document
        // with: the policy is already as complete as it gets.
        if (discover is null || SecurityHeaders.ProviderConnectSources(provider, authority).Count == 0)
        {
            _complete = _partial;
        }
    }

    public async ValueTask<string> GetAsync()
    {
        if (_complete is { } complete)
        {
            return complete;
        }

        Task<string?> attempt;
        bool wait;
        lock (_gate)
        {
            if (_complete is { } done)
            {
                return done;
            }

            if (_attempt is null || (_attempt.IsCompleted && _time.GetUtcNow() >= _retryAt))
            {
                _attempt = DiscoverAsync();
            }
            attempt = _attempt;
            wait = !_failed;
        }

        if (wait)
        {
            await Task.WhenAny(attempt, Task.Delay(FirstWait, _time));
        }
        return attempt.IsCompletedSuccessfully && attempt.Result is { } policy ? policy : _partial;
    }

    private async Task<string?> DiscoverAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(FetchTimeout, _time);
            var metadata = await _discover!(timeout.Token);
            var policy = SecurityHeaders.ContentSecurityPolicyFor(_provider, _authority, metadata);
            _complete = policy;
            return policy;
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _failed = true;
                _retryAt = _time.GetUtcNow() + RetryAfter;
            }
            _logger.LogWarning(e,
                "Couldn't read the identity provider's discovery document for the content security policy. " +
                "Browser sign-in may fail until it can be read; retrying in {RetryAfter}.", RetryAfter);
            return null;
        }
    }
}
