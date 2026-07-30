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
    /// <para><c>connect-src</c> spells out ws/wss as well as <c>'self'</c>
    /// because the SignalR hub falls back to a WebSocket on the same origin,
    /// and not every browser resolves <c>'self'</c> to the ws scheme.</para>
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval' https://accounts.google.com; " +
        "style-src 'self' 'unsafe-inline' https://accounts.google.com; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss: https://accounts.google.com; " +
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
                // frame-ancestors covers this for current browsers; the legacy
                // header is free and still honoured by some proxies.
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                // Google Identity Services signs in through a popup, which a
                // plain same-origin COOP would sever.
                headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
                return Task.CompletedTask;
            }, context);

            return next();
        });
}
