namespace Solo.Api;

/// <summary>
/// Rate-limit policy names referenced by <c>[EnableRateLimiting]</c> on
/// controllers; the policies themselves are configured in Program.cs.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// Strict per-IP window for endpoints that check credentials or allocate
    /// server state: setup-credential verification (brute-forceable), session
    /// minting, and desktop sign-in flow creation.
    /// </summary>
    public const string Auth = "auth";

    /// <summary>
    /// Per-IP window sized for the desktop shell's sign-in polling (every 2s
    /// while a flow is pending — see main/desktop-auth.js POLL_INTERVAL_MS).
    /// </summary>
    public const string AuthPoll = "auth-poll";
}

/// <summary>Partition keys shared by the rate-limit policies.</summary>
internal static class RateLimitPartitions
{
    /// <summary>
    /// Authenticated callers are metered per user (both the bearer and the
    /// solo_ session scheme emit <c>sub</c>); anonymous callers per client IP.
    /// </summary>
    public static string Client(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true &&
        context.User.FindFirst("sub")?.Value is { Length: > 0 } sub
            ? $"user:{sub}"
            : Ip(context);

    /// <summary>
    /// Per client IP — the real client address only when UseForwardedHeaders
    /// has applied X-Forwarded-For (production sits behind Nginx Proxy Manager,
    /// so without it every caller would share the proxy's address).
    /// </summary>
    public static string Ip(HttpContext context) =>
        $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
