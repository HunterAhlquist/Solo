namespace Solo.Api.Auth;

public static class AuthConstants
{
    /// <summary>
    /// OIDC ID-token validation (JWT bearer against the provider) — and the
    /// default scheme. Its forward selector hands <c>solo_</c> tokens to
    /// <see cref="SessionScheme"/> instead.
    /// </summary>
    public const string BearerScheme = "Bearer";

    /// <summary>Long-lived Solo session tokens (see SessionAuthenticationHandler).</summary>
    public const string SessionScheme = "SoloSession";

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
