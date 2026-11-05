namespace Solo.Api.Auth;

/// <summary>
/// The web app's <c>solo_</c> session, carried in an HttpOnly cookie instead of
/// a bearer header, so no script on the page (an XSS payload included) can read
/// it. The desktop app keeps its bearer token: its pages are served from a local
/// origin that only proxies the API.
/// </summary>
/// <remarks>
/// <para>Two names, because a browser-session cookie (the user unchecked "stay
/// signed in") has to stay one when the sliding expiry re-issues it, and the
/// server keeps nothing that says which kind a session was given as. The
/// <c>__Host-</c> prefix makes the browser refuse one set by a sibling
/// subdomain or without <c>Secure</c>.</para>
///
/// <para>The cookie counts only on a request that also carries
/// <see cref="RequestHeader"/>. SameSite=Strict already keeps other sites from
/// sending it, but a same-site page (another subdomain) still could, and a page
/// can't add a custom header to a cross-origin request without a CORS preflight
/// this API never answers. It also keeps the cookie off the hub's WebSocket,
/// which takes a ticket instead (see <see cref="HubTickets"/>).</para>
/// </remarks>
public static class SessionCookie
{
    public const string PersistentName = "__Host-solo-session";
    public const string BrowserSessionName = "__Host-solo-tab-session";

    /// <summary>Sent on every API call by the web client (Solo.Client's IdTokenMessageHandler and solo-interop.js).</summary>
    public const string RequestHeader = "X-Solo-Client";

    /// <summary>
    /// The session token from the cookie, and whether it is the persistent
    /// one. Null unless the request has <see cref="RequestHeader"/> and no
    /// Authorization header, so a request is authenticated by one credential.
    /// </summary>
    public static (string Token, bool Persistent)? FromRequest(HttpRequest request)
    {
        if (!request.Headers.ContainsKey(RequestHeader) || request.Headers.Authorization.Count > 0)
        {
            return null;
        }

        if (request.Cookies[PersistentName] is { } persistent && SessionTokens.IsSessionToken(persistent))
        {
            return (persistent, true);
        }

        return request.Cookies[BrowserSessionName] is { } browserSession && SessionTokens.IsSessionToken(browserSession)
            ? (browserSession, false)
            : null;
    }

    /// <summary>Sets the cookie; a browser-session cookie has no expiry and goes when the browser closes.</summary>
    public static void Append(HttpResponse response, string token, bool persistent, DateTimeOffset expiresAt)
    {
        var options = Options();
        options.Expires = persistent ? expiresAt : null;
        response.Cookies.Append(persistent ? PersistentName : BrowserSessionName, token, options);
        response.Cookies.Delete(persistent ? BrowserSessionName : PersistentName, Options());
    }

    public static void Delete(HttpResponse response)
    {
        response.Cookies.Delete(PersistentName, Options());
        response.Cookies.Delete(BrowserSessionName, Options());
    }

    // Secure whatever Request.IsHttps says: behind a TLS-terminating proxy the
    // app itself serves plain HTTP. Browsers accept Secure cookies from
    // http://localhost, so development still works.
    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
