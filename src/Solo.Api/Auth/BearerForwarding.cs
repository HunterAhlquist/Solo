namespace Solo.Api.Auth;

/// <summary>
/// The default "Bearer" scheme's forward rule: which of Solo's own credentials
/// a request carries, so the scheme that understands it validates it instead of
/// the OIDC handler. Shared with the test host, which swaps the OIDC handler
/// for a header-driven one but has to route Solo's credentials as deployed.
/// </summary>
public static class BearerForwarding
{
    public static string? Select(HttpContext context) =>
        HubTickets.FromRequest(context.Request) is not null ? AuthConstants.HubTicketScheme
        : SessionTokens.IsSessionToken(SessionTokens.FromRequest(context.Request)) ? AuthConstants.SessionScheme
        : null;
}
