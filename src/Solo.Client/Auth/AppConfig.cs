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

    /// <summary>Every other provider (Microsoft, generic OIDC) does code + PKCE properly.</summary>
    public bool UsesOidcRedirect => ProviderConfigured && !IsGoogle;
}
