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

    /// <summary>Claim carrying the AuthSession id on session-authenticated principals.</summary>
    public const string SessionIdClaim = "solo:session";

    public const string SetupScheme = "Setup";

    public const string SetupAdminRole = "SetupAdmin";

    public const string SuperAdminPolicy = "SuperAdmin";
    public const string SetupPolicy = "SetupAdmin";
    public const string ProjectReadPolicy = "Project:Read";
    public const string ProjectWritePolicy = "Project:Write";
    public const string ProjectOwnerPolicy = "Project:Owner";
}
