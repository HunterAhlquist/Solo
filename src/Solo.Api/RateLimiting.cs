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
    /// Per person, for the endpoints that send an invitation: adding someone
    /// to a project by email, and sending a pending invite again. Either puts
    /// mail from the instance in the inbox of whoever the caller names, with
    /// the caller's own name and the project's in it, so how much of it one
    /// person can cause is held to what inviting a team takes.
    /// </summary>
    public const string Invites = "invites";
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
