using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Solo.Api.Auth;

/// <summary>
/// Authenticates <see cref="HubTickets"/> on the hub path. The principal carries
/// the claims a session principal does, built from the user the ticket was
/// minted for, so user resolution downstream is oblivious to which credential
/// arrived. Its authentication type is its own: a ticket is neither a provider
/// sign-in nor a session, and mints nothing (see <see cref="CredentialKind"/>).
/// </summary>
public sealed class HubTicketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    HubTickets tickets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (HubTickets.FromRequest(Request) is not (var token, var fromUrl))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (tickets.Redeem(token, fromUrl) is not { } holder)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown, expired or spent hub ticket."));
        }

        var claims = new List<Claim>
        {
            new("iss", holder.Issuer),
            new("sub", holder.Subject),
        };
        if (holder.SessionId is { } sessionId)
        {
            // So revoking the session can find the connection it opened.
            claims.Add(new Claim(AuthConstants.SessionIdClaim, sessionId.ToString()));
        }

        if (holder.Email.Length > 0)
        {
            claims.Add(new Claim("email", holder.Email));
        }

        if (holder.DisplayName is not null)
        {
            claims.Add(new Claim("name", holder.DisplayName));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name, nameType: "name", roleType: "role");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
