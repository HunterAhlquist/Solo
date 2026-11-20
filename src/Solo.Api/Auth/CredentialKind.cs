using System.Security.Claims;

namespace Solo.Api.Auth;

/// <summary>
/// Which kind of credential authenticated a request, read from the identity the
/// authenticating handler built — never from a claim a token could carry.
/// </summary>
/// <remarks>
/// Rules about minting and managing credentials are allowlists on this, not
/// denylists of the credentials known to be wrong. Refusing only <c>solo_</c>
/// sessions let an extension's credential (an MCP token) mint one, and the two
/// could then renew each other forever: revoke either and the other buys a
/// replacement.
/// </remarks>
public static class CredentialKind
{
    /// <summary>A token the identity provider issued, validated just now by the OIDC scheme.</summary>
    public static bool IsProviderSignIn(this ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == AuthConstants.OidcAuthenticationType;

    /// <summary>
    /// A session itself, from an app's header or the web app's cookie: not a
    /// provider sign-in, and not something issued on a session's behalf.
    /// </summary>
    public static bool IsSession(this ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == AuthConstants.SessionScheme;

    /// <summary>
    /// What an app hands a page it hosts (<see cref="HostedSessions"/>). Not a
    /// Solo sign-in, deliberately: it is admitted by name where a page needs
    /// it, and nowhere credentials are minted or managed.
    /// </summary>
    public static bool IsHostedSession(this ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == AuthConstants.HostedAuthenticationType;

    /// <summary>A provider sign-in or a Solo session: the credentials core itself trusts or issues.</summary>
    public static bool IsSoloSignIn(this ClaimsPrincipal user) =>
        user.IsProviderSignIn() || user.Identity?.AuthenticationType == AuthConstants.SessionScheme;
}
