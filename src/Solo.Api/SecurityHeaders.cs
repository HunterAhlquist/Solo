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
    /// <para><c>connect-src</c> is <c>'self'</c> only. The SignalR hub's
    /// WebSocket is same-origin, and CSP Level 3 resolves that through
    /// <c>'self'</c> — it matches the ws/wss form of the page's own origin.
    /// Bare <c>ws:</c>/<c>wss:</c> scheme sources are deliberately avoided:
    /// they permit a socket to <em>any</em> host, which would hand a script
    /// that slipped past the other controls a ready exfiltration channel.</para>
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval' https://accounts.google.com; " +
        "style-src 'self' 'unsafe-inline' https://accounts.google.com; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' https://accounts.google.com; " +
        "frame-src https://accounts.google.com; " +
        "worker-src 'self' blob:; " +
        // Nothing here is meant to be framed, submit off-site, or rebase its
        // relative URLs — an injected <base> would repoint every asset.
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseSoloSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var headers = ((HttpContext)state).Response.Headers;

                // Endpoints that serve user-uploaded bytes set their own,
                // stricter policy (see ImagesController); don't overwrite it.
                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    headers.ContentSecurityPolicy = ContentSecurityPolicy;
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
