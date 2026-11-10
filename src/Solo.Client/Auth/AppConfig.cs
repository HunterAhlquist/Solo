using Solo.Shared.Dtos;

namespace Solo.Client.Auth;

/// <summary>
/// The anonymous bootstrap payload, fetched once before the host is built. The
/// OIDC provider is configured in exactly one place — the API's environment —
/// and arrives here.
/// </summary>
public sealed class AppConfig
{
    public AuthConfigDto? Auth { get; init; }

    /// <summary>
    /// The instance's theme, fetched alongside the auth config so the very first
    /// paint is already themed. Null if the instance answered the auth call but
    /// not this one — the client falls back to a built-in palette rather than
    /// refusing to boot over branding.
    /// </summary>
    public ThemeDto? Theme { get; init; }

    /// <summary>Set when the instance could not be reached at startup.</summary>
    public string? LoadError { get; init; }

    /// <summary>
    /// What the instance calls itself, where its operator gave it a name. Read
    /// once at bootstrap, like the theme: changing it reloads the page.
    /// </summary>
    public bool HasOwnName => !string.IsNullOrWhiteSpace(Auth?.DisplayName);

    /// <summary>
    /// The name to put wherever the app names itself — the app bar, the sign-in
    /// page, page titles. The instance's own where it has one, and otherwise
    /// the product's.
    /// </summary>
    public string SiteName => HasOwnName ? Auth!.DisplayName! : InstanceBranding.ProductName;

    /// <summary>The line under the name on the sign-in page: the instance's own, or the product's.</summary>
    public string Tagline =>
        string.IsNullOrWhiteSpace(Auth?.Tagline) ? InstanceBranding.DefaultTagline : Auth.Tagline;

    /// <summary>
    /// Mutable: the setup wizard flips this the moment handoff succeeds, so the
    /// router stops forcing every route to <c>/setup</c> without a page reload.
    /// </summary>
    public bool SetupCompleted { get; set; }

    /// <summary>
    /// False on a fresh deployment whose operator hasn't supplied
    /// <c>Auth:Authority</c>/<c>Auth:ClientId</c> yet. Sign-in is impossible
    /// until they do, so we don't even register the OIDC provider.
    /// </summary>
    public bool ProviderConfigured =>
        Auth is { Authority.Length: > 0, ClientId.Length: > 0 };

    /// <summary>Display name for the sign-in button, e.g. "Google".</summary>
    public string ProviderName =>
        string.IsNullOrWhiteSpace(Auth?.Provider) ? "your provider" : Auth.Provider;

    /// <summary>
    /// Google is the odd one out. Its "Web application" OAuth clients demand a
    /// <c>client_secret</c> at the token endpoint even with PKCE — a secret a
    /// browser cannot hold — so the code flow is unusable from a WASM client.
    /// Instead we use Google Identity Services, which hands back an ID token
    /// directly with no secret and no token-endpoint call. That is exactly what the
    /// API validates, so nothing downstream changes.
    /// </summary>
    public bool IsGoogle =>
        ProviderConfigured && string.Equals(Auth!.Provider, "google", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A desktop or mobile app hosts the page. Read once at bootstrap: the
    /// app's bridge object exists before any of the page's scripts run.
    /// </summary>
    public bool InApp { get; init; }

    /// <summary>
    /// Signed in through a Solo session rather than the web client's OIDC
    /// library: Google always (see <see cref="IsGoogle"/>), and every provider
    /// inside an app. An app signs in on the instance's <c>/desktop-auth</c>
    /// page, in the system's sign-in sheet or browser, and gets a session
    /// back, because its web view can't run the provider's pages itself: Google
    /// refuses to, the iPad app opens other hosts in Safari, and the desktop
    /// shell's CSP allows no provider but Google. The provider's flow then runs
    /// on the instance's own origin, whichever provider it is.
    /// </summary>
    public bool UsesSessionSignIn => IsGoogle || (ProviderConfigured && InApp);

    /// <summary>Every other provider (Microsoft, generic OIDC) does code + PKCE properly, on the web.</summary>
    public bool UsesOidcRedirect => ProviderConfigured && !UsesSessionSignIn;
}
