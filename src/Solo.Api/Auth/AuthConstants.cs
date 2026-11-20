namespace Solo.Api.Auth;

public static class AuthConstants
{
    /// <summary>
    /// OIDC ID-token validation (JWT bearer against the provider) — and the
    /// default scheme. Its forward selector (<see cref="BearerForwarding"/>) hands
    /// Solo's own credentials to <see cref="SessionScheme"/> and
    /// <see cref="HubTicketScheme"/> instead.
    /// </summary>
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// <c>AuthenticationType</c> of an identity the OIDC scheme validated from a
    /// provider token — what <see cref="CredentialKind.IsProviderSignIn"/> tests.
    /// Set explicitly so it is ours to rely on, not the token handler's default.
    /// </summary>
    public const string OidcAuthenticationType = "SoloOidc";

    /// <summary>Long-lived Solo session tokens (see SessionAuthenticationHandler).</summary>
    public const string SessionScheme = "SoloSession";

    /// <summary>Short-lived tickets for the hub's URL (see HubTickets).</summary>
    public const string HubTicketScheme = "SoloHubTicket";

    /// <summary>An extension's background work acting as a user who granted it (contract 1.8).</summary>
    public const string BackgroundScheme = Solo.Extensions.BackgroundTokens.Scheme;

    /// <summary>Claim carrying the AuthSession id on session-authenticated principals.</summary>
    public const string SessionIdClaim = "solo:session";

    /// <summary>
    /// <c>AuthenticationType</c> of an identity built from a hosted session
    /// (<see cref="HostedSessions"/>): the session scheme validated it, but it
    /// is not a session, and <see cref="CredentialKind"/> keeps the two apart.
    /// </summary>
    public const string HostedAuthenticationType = "SoloHosted";

    /// <summary>
    /// The session a hosted session stands for. Its own claim and not
    /// <see cref="SessionIdClaim"/>: what acts on "the caller's session"
    /// (revoking it, naming it as the current one) must not find one here.
    /// </summary>
    public const string HostedParentClaim = "solo:hosted-parent";

    public const string SetupScheme = "Setup";

    public const string SetupAdminRole = "SetupAdmin";

    public const string SuperAdminPolicy = "SuperAdmin";
    public const string SetupPolicy = "SetupAdmin";
    public const string ProjectReadPolicy = "Project:Read";
    public const string ProjectWritePolicy = "Project:Write";
    public const string ProjectOwnerPolicy = "Project:Owner";
}
