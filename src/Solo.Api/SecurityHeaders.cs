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
    /// <see cref="ProviderOrigin"/>). The SignalR hub's WebSocket is
    /// same-origin, and CSP Level 3 resolves that through <c>'self'</c> — it
    /// matches the ws/wss form of the page's own origin. Bare
    /// <c>ws:</c>/<c>wss:</c> scheme sources are deliberately avoided: they
    /// permit a socket to <em>any</em> host, which would hand a script that
    /// slipped past the other controls a ready exfiltration channel.</para>
    /// </summary>
    private static string ContentSecurityPolicy(string? providerOrigin) =>
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval' https://accounts.google.com; " +
        "style-src 'self' 'unsafe-inline' https://accounts.google.com; " +
        "img-src 'self' data: https: blob:; " +
        "font-src 'self' data:; " +
        $"connect-src 'self' https://accounts.google.com{(providerOrigin is null ? "" : " " + providerOrigin)}; " +
        "frame-src https://accounts.google.com; " +
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
    /// The origin a code + PKCE sign-in calls from the browser: the provider's
    /// discovery document and token endpoint, fetched by the web client's OIDC
    /// library and by the apps' <c>/desktop-auth</c> page. Without it in
    /// <c>connect-src</c> the first of those fetches is blocked, and every
    /// Microsoft or generic sign-in ends in a network error.
    /// </summary>
    /// <remarks>
    /// Null for Google, whose sign-in never calls a token endpoint, and for
    /// anything that isn't a plain https origin: the value goes into a header,
    /// so it is rebuilt from a parsed URI rather than passed through. Plain
    /// http is allowed for a loopback host only, for a provider run locally.
    /// A provider whose token endpoint sits on another origin than its issuer
    /// isn't covered; none of the documented ones does that.
    /// </remarks>
    public static string? ProviderOrigin(string? provider, string? authority)
    {
        if (string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(authority, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            (uri.Scheme == "http" && !uri.IsLoopback) ||
            uri.UserInfo.Length > 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The app's policy for an instance signing in with this provider.</summary>
    public static string ContentSecurityPolicyFor(string? provider, string? authority) =>
        ContentSecurityPolicy(ProviderOrigin(provider, authority));

    public static IApplicationBuilder UseSoloSecurityHeaders(this IApplicationBuilder app, IConfiguration config)
    {
        // Read once: the provider is fixed for the life of the process.
        var policy = ContentSecurityPolicyFor(config["Auth:Provider"], config["Auth:Authority"]);
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(state =>
            {
                var headers = ((HttpContext)state).Response.Headers;

                // Endpoints that serve bytes someone else chose set their own,
                // stricter policy (IsolationPolicy); don't overwrite it.
                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    headers.ContentSecurityPolicy = policy;
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
                headers.XFrameOptions = "DENY";
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
                return Task.CompletedTask;
            }, context);

            return next();
        });
    }
}
