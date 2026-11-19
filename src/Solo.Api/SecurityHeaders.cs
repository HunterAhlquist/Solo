using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Solo.Api;

/// <summary>
/// Response headers the whole instance carries. Defence in depth behind the
/// input handling — an XSS that slips past the markdown pipeline or the image
/// isolation policy still has to get past this.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The app's content security policy.
    ///
    /// <para><c>'wasm-unsafe-eval'</c> is what Blazor WebAssembly needs to
    /// compile the .NET runtime; it permits WASM only, not <c>eval</c>.
    /// MudBlazor writes inline styles (popover positioning, theme variables),
    /// hence <c>'unsafe-inline'</c> under style-src — a style-only relaxation,
    /// which is not a script vector.</para>
    ///
    /// <para>The accounts.google.com entries are Google Identity Services'
    /// documented requirements: the client script, its One Tap iframe, its
    /// stylesheet, and the calls it makes. They cost nothing on an instance
    /// configured for another provider, which never loads that script.</para>
    ///
    /// <para>Blob images let a page fetch a members-only image with its sign-in
    /// credential and display an object URL; only script already running on
    /// the page can mint that URL.</para>
    ///
    /// <para><c>connect-src</c> is <c>'self'</c> and Google, plus the
    /// instance's own identity provider when that isn't Google (see
    /// <see cref="ProviderConnectSources"/>). The SignalR hub's WebSocket is
    /// same-origin, and CSP Level 3 resolves that through <c>'self'</c> — it
    /// matches the ws/wss form of the page's own origin. Bare
    /// <c>ws:</c>/<c>wss:</c> scheme sources are deliberately avoided: they
    /// permit a socket to <em>any</em> host, which would hand a script that
    /// slipped past the other controls a ready exfiltration channel.</para>
    /// </summary>
    private static string ContentSecurityPolicy(
        IReadOnlyCollection<string> providerConnect, IReadOnlyCollection<string> providerFrames) =>
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval' https://accounts.google.com; " +
        "style-src 'self' 'unsafe-inline' https://accounts.google.com; " +
        "img-src 'self' data: https: blob:; " +
        "font-src 'self' data:; " +
        $"connect-src 'self' https://accounts.google.com{string.Concat(providerConnect.Select(o => " " + o))}; " +
        // 'self' with a provider's origins: its renewal iframe comes back to
        // our own callback page, and the embedder's frame-src governs that
        // redirect as well as the frame's first load.
        $"frame-src https://accounts.google.com{(providerFrames.Count > 0 ? " 'self'" : "")}{string.Concat(providerFrames.Select(o => " " + o))}; " +
        "worker-src 'self' blob:; " +
        // Nothing here is meant to be framed, submit off-site, or rebase its
        // relative URLs — an injected <base> would repoint every asset.
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    /// <summary>
    /// For responses whose bytes someone else chose: uploaded images, and
    /// avatars fetched from the provider's picture URL. They are served from the
    /// app's own origin, and an SVG opened as a document (not through
    /// <c>&lt;img&gt;</c>, which never runs script) is a document on that origin,
    /// so a scripted one would be stored XSS against whoever follows the link,
    /// with the session token one localStorage read away. <c>default-src 'none'</c>
    /// blocks every subresource and script the file might reach for, and
    /// <c>sandbox</c> drops the response into an opaque origin so even inline
    /// script has nothing to steal. Neither affects the <c>&lt;img&gt;</c> path
    /// the app actually uses.
    /// </summary>
    public const string IsolationPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    /// <summary>
    /// The origins a code + PKCE sign-in calls from the browser, for a provider
    /// other than Google: its discovery document (at the authority), and the
    /// token, userinfo and key endpoints the document names. The web client's
    /// OIDC library calls all of them, the apps' <c>/desktop-auth</c> page the
    /// first two; without them in <c>connect-src</c> the sign-in ends in a
    /// network error. They needn't share an origin — Entra ID's userinfo
    /// endpoint is on graph.microsoft.com — so the endpoints come from the
    /// document rather than from the authority.
    /// </summary>
    /// <remarks>
    /// Empty for Google, whose sign-in never calls a token endpoint. Anything
    /// that isn't a plain https origin is left out: these go into a header, so
    /// each is rebuilt from a parsed URI rather than passed through. Plain http
    /// is allowed for a loopback host only, for a provider run locally.
    /// </remarks>
    public static IReadOnlyCollection<string> ProviderConnectSources(
        string? provider, string? authority, OpenIdConnectConfiguration? metadata = null) =>
        ProviderOrigins(provider, authority, metadata?.TokenEndpoint, metadata?.UserInfoEndpoint, metadata?.JwksUri);

    /// <summary>
    /// The origins the web client's OIDC library frames: the authorization
    /// endpoint, which renews the sign-in silently in a hidden iframe before
    /// the ID token runs out, and the provider's session-check page where it
    /// has one. Without them renewal fails, and the person is sent back to
    /// sign in about an hour after they did.
    /// </summary>
    public static IReadOnlyCollection<string> ProviderFrameSources(
        string? provider, string? authority, OpenIdConnectConfiguration? metadata = null) =>
        ProviderOrigins(provider, authority, metadata?.AuthorizationEndpoint, metadata?.CheckSessionIframe);

    private static IReadOnlyCollection<string> ProviderOrigins(
        string? provider, string? authority, params string?[] endpoints)
    {
        if (string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase) ||
            SafeOrigin(authority) is not { } issuer)
        {
            return [];
        }

        var origins = new List<string> { issuer };
        foreach (var endpoint in endpoints)
        {
            if (SafeOrigin(endpoint) is { } origin && !origins.Contains(origin))
            {
                origins.Add(origin);
            }
        }
        return origins;
    }

    private static string? SafeOrigin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" &&
        (uri.Scheme == "https" || uri.IsLoopback) &&
        uri.UserInfo.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    /// <summary>
    /// The app's policy for an instance signing in with this provider, from
    /// its discovery document where that has been read.
    /// </summary>
    public static string ContentSecurityPolicyFor(
        string? provider, string? authority, OpenIdConnectConfiguration? metadata = null) =>
        ContentSecurityPolicy(
            ProviderConnectSources(provider, authority, metadata),
            ProviderFrameSources(provider, authority, metadata));

    /// <summary>
    /// Where the web client's OIDC library lands after a sign-in, and where its
    /// silent renewal lands too: Blazor sets no separate silent redirect, so
    /// the hidden iframe the library renews in comes back to this page.
    /// </summary>
    public const string OidcCallbackPath = "/authentication/login-callback";

    /// <summary>
    /// The policy for the OIDC callback page, which has to load inside the
    /// app's own renewal iframe: <c>frame-ancestors 'self'</c> instead of
    /// <c>'none'</c>. Only that page, and only same-origin — nothing else of
    /// ours is ever framed, and nobody else may frame it.
    /// </summary>
    public static string AllowFramingBySelf(string policy) =>
        policy.Replace("frame-ancestors 'none'", "frame-ancestors 'self'", StringComparison.Ordinal);

    public static IApplicationBuilder UseSoloSecurityHeaders(this IApplicationBuilder app)
    {
        var policies = app.ApplicationServices.GetRequiredService<ContentSecurityPolicies>();
        return app.Use((context, next) =>
        {
            // The renewal iframe's landing page, on an instance whose web
            // client renews that way. Read on the way in: the SPA fallback
            // rewrites the path to index.html before the response starts.
            var framedBySelf = policies.RenewsInAFrame &&
                context.Request.Path.Equals(OidcCallbackPath, StringComparison.OrdinalIgnoreCase);

            context.Response.OnStarting(async state =>
            {
                var headers = ((HttpContext)state).Response.Headers;

                // Endpoints that serve bytes someone else chose set their own,
                // stricter policy (IsolationPolicy); don't overwrite it.
                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    var policy = await policies.GetAsync();
                    headers.ContentSecurityPolicy = framedBySelf ? AllowFramingBySelf(policy) : policy;
                }

                headers.XContentTypeOptions = "nosniff";
                // Solo is a private application: keep every response out of
                // search indexes and AI overviews. Unlike a <meta> tag this
                // covers non-HTML responses (JSON, files) too, and unlike
                // robots.txt it actually forces de-indexing — a crawler allowed
                // to fetch the URL sees this and drops it. robots.txt names the
                // AI crawlers that ignore it; the edge (Cloudflare) catches the
                // ones that ignore both.
                headers["X-Robots-Tag"] = "noindex, nofollow";
                // frame-ancestors covers this for current browsers; the legacy
                // header is free and still honoured by some proxies.
                headers.XFrameOptions = framedBySelf ? "SAMEORIGIN" : "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                // Google Identity Services signs in through a popup, which a
                // plain same-origin COOP would sever.
                headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
                // HSTS. TLS terminates at the reverse proxy, so the app itself
                // serves plain HTTP and Request.IsHttps is false here — the
                // stock UseHsts() middleware would never emit this. Set it
                // directly: the proxy forwards it to the browser over HTTPS,
                // where it is honoured (a user agent ignores an HSTS header
                // received over plain HTTP, so this is harmless behind the
                // proxy too). No 'preload' — that is a hard-to-reverse
                // commitment the operator should opt into deliberately.
                headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
                // The app uses none of these powerful features; deny them so a
                // script injected anywhere on the origin cannot reach for them.
                headers["Permissions-Policy"] =
                    "accelerometer=(), autoplay=(), camera=(), display-capture=(), " +
                    "encrypted-media=(), geolocation=(), gyroscope=(), magnetometer=(), " +
                    "microphone=(), midi=(), payment=(), usb=()";
            }, context);

            return next();
        });
    }
}
