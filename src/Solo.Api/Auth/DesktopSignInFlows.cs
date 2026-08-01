using System.Security.Cryptography;
using Solo.Shared.Dtos;

namespace Solo.Api.Auth;

/// <summary>
/// Pending desktop sign-ins, device-code style. The shell opens a flow and
/// gets two secrets: the <c>state</c> identifies the flow in the browser
/// sign-in URL, while the <c>device code</c> never leaves the shell and is
/// the only thing that can redeem the finished sign-in — so a state leaked
/// through browser history or logs is useless on its own. Flows live in
/// memory: they expire in minutes, and Solo runs as a single instance.
/// </summary>
public sealed class DesktopSignInFlows(TimeSpan? ttl = null)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

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
        public required string DeviceCode { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public string? Description { get; init; }

        /// <summary>Which client opened the flow, for the per-client pending cap.</summary>
        public required string ClientKey { get; init; }

        /// <summary>Set when the browser sign-in verified this user; null while pending.</summary>
        public Guid? UserId { get; set; }
    }

    public enum PollStatus { Unknown, Pending, Completed }

    /// <summary>
    /// Opens a flow for the given client, or returns null when the global or
    /// per-client pending cap is hit. <paramref name="clientKey"/> is the
    /// caller's IP (see the controller), so no single source can monopolise
    /// the pool.
    /// </summary>
    public DesktopAuthFlowDto? Start(string? description, string clientKey)
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
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                DateTimeOffset.UtcNow + _ttl);
            _byState.Add(flow.State, new Flow
            {
                DeviceCode = flow.DeviceCode,
                ExpiresAt = flow.ExpiresAt,
                Description = description,
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
    /// Attaches the verified user to the flow. False when the state is
    /// unknown, expired, or already completed — completion is single-use.
    /// </summary>
    public bool TryComplete(string? state, Guid userId)
    {
        lock (_lock)
        {
            Sweep();
            if (state is null || !_byState.TryGetValue(state, out var flow) || flow.UserId is not null)
            {
                return false;
            }

            flow.UserId = userId;
            return true;
        }
    }

    /// <summary>
    /// The shell's poll. A completed flow is consumed: its user is handed out
    /// exactly once, so the device code cannot redeem two sessions.
    /// </summary>
    public PollStatus Poll(string? deviceCode, out Guid userId, out string? description)
    {
        userId = default;
        description = null;
        lock (_lock)
        {
            Sweep();
            if (deviceCode is null)
            {
                return PollStatus.Unknown;
            }

            foreach (var (state, flow) in _byState)
            {
                if (flow.DeviceCode != deviceCode)
                {
                    continue;
                }

                if (flow.UserId is not { } completedUser)
                {
                    return PollStatus.Pending;
                }

                _byState.Remove(state);
                userId = completedUser;
                description = flow.Description;
                return PollStatus.Completed;
            }

            return PollStatus.Unknown;
        }
    }

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
