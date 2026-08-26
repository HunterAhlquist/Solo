using System.Security.Cryptography;
using System.Text;
using Solo.Shared.Dtos;

namespace Solo.Api.Auth;

/// <summary>
/// Pending desktop sign-ins. The shell opens a loopback listener and gives the
/// instance its port plus a PKCE challenge. After browser authentication, the
/// instance sends a one-time authorization code to that loopback listener;
/// only the process holding the verifier can redeem it. Flows live in memory:
/// they expire in minutes, and Solo runs as a single instance.
/// </summary>
public sealed class DesktopSignInFlows(TimeSpan? ttl = null)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    public const string LoopbackPath = "/desktop-auth/callback";

    /// <summary>Anonymous callers can open flows; cap them so they can't grow memory.</summary>
    private const int MaxPending = 100;

    /// <summary>
    /// Pending flows a single client (keyed by IP) may hold at once. Without
    /// this, one source could fill <see cref="MaxPending"/> by itself: the
    /// per-IP request limit still lets it open flows steadily, and over the
    /// 10-minute TTL that is enough to reach 100 and lock out every other
    /// user's desktop sign-in. The per-client cap keeps one address to a small
    /// slice of the pool, so exhausting it takes many distinct addresses.
    /// </summary>
    private const int MaxPendingPerClient = 10;

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Flow> _byState = new();

    private sealed class Flow
    {
        public required DateTimeOffset ExpiresAt { get; init; }
        public string? Description { get; init; }
        public required string CodeChallenge { get; init; }
        public required int LoopbackPort { get; init; }

        /// <summary>Which client opened the flow, for the per-client pending cap.</summary>
        public required string ClientKey { get; init; }

        /// <summary>Set together after the browser has verified the user.</summary>
        public Guid? UserId { get; set; }
        public string? AuthorizationCodeHash { get; set; }
    }

    /// <summary>
    /// Opens a flow for the given client, or returns null when the global or
    /// per-client pending cap is hit. <paramref name="clientKey"/> is the
    /// caller's IP (see the controller), so no single source can monopolise
    /// the pool.
    /// </summary>
    public DesktopAuthFlowDto? Start(
        string? description,
        string codeChallenge,
        int loopbackPort,
        string clientKey)
    {
        lock (_lock)
        {
            Sweep();
            if (_byState.Count >= MaxPending)
            {
                return null;
            }

            var fromClient = 0;
            foreach (var existing in _byState.Values)
            {
                if (existing.ClientKey == clientKey && ++fromClient >= MaxPendingPerClient)
                {
                    return null;
                }
            }

            var flow = new DesktopAuthFlowDto(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                DateTimeOffset.UtcNow + _ttl);
            _byState.Add(flow.State, new Flow
            {
                ExpiresAt = flow.ExpiresAt,
                Description = description,
                CodeChallenge = codeChallenge,
                LoopbackPort = loopbackPort,
                ClientKey = clientKey,
            });
            return flow;
        }
    }

    /// <summary>True while the state's sign-in page may still be served.</summary>
    public bool IsPending(string? state)
    {
        lock (_lock)
        {
            Sweep();
            return state is not null && _byState.TryGetValue(state, out var flow) && flow.UserId is null;
        }
    }

    /// <summary>
    /// Attaches the verified user to the flow and returns the one-time code to
    /// the loopback listener. False when the state is unknown, expired, or
    /// already completed — browser completion is single-use.
    /// </summary>
    public bool TryComplete(
        string? state,
        Guid userId,
        out DesktopAuthCompletionDto? completion)
    {
        completion = null;
        lock (_lock)
        {
            Sweep();
            if (state is null || !_byState.TryGetValue(state, out var flow) || flow.UserId is not null)
            {
                return false;
            }

            var authorizationCode = Base64Url(RandomNumberGenerator.GetBytes(32));
            flow.UserId = userId;
            flow.AuthorizationCodeHash = HashAuthorizationCode(authorizationCode);
            var callback = new UriBuilder(
                Uri.UriSchemeHttp,
                "127.0.0.1",
                flow.LoopbackPort,
                LoopbackPath,
                $"?code={authorizationCode}&state={state}");
            completion = new DesktopAuthCompletionDto(callback.Uri.AbsoluteUri);
            return true;
        }
    }

    /// <summary>
    /// Redeems the browser-delivered code when both it and the verifier match
    /// the pending flow. A successful flow is consumed exactly once.
    /// </summary>
    public bool TryRedeem(
        string? state,
        string? authorizationCode,
        string? codeVerifier,
        out Guid userId,
        out string? description)
    {
        userId = default;
        description = null;
        lock (_lock)
        {
            Sweep();
            if (state is null || authorizationCode is null || codeVerifier is null ||
                !_byState.TryGetValue(state, out var flow) ||
                flow.UserId is not { } completedUser ||
                flow.AuthorizationCodeHash is not { } expectedCodeHash)
            {
                return false;
            }

            var suppliedCodeHash = HashAuthorizationCode(authorizationCode);
            var suppliedChallenge = PkceChallenge(codeVerifier);
            if (!FixedTimeEquals(expectedCodeHash, suppliedCodeHash) ||
                !FixedTimeEquals(flow.CodeChallenge, suppliedChallenge))
            {
                return false;
            }

            _byState.Remove(state);
            userId = completedUser;
            description = flow.Description;
            return true;
        }
    }

    /// <summary>RFC 7636 S256 challenge for a verifier held by the desktop process.</summary>
    public static string PkceChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string HashAuthorizationCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(code)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string expected, string supplied) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(supplied));

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (state, flow) in _byState)
        {
            if (flow.ExpiresAt < now)
            {
                _byState.Remove(state);
            }
        }
    }
}
