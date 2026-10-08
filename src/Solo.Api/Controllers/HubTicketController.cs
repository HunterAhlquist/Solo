using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solo.Api.Auth;

namespace Solo.Api.Controllers;

/// <summary>
/// The ticket a client puts in the hub's URL in place of its real credential,
/// minted just before it connects (see <see cref="HubTickets"/>).
/// </summary>
[Route("api/auth/hub-ticket")]
[Authorize]
public sealed class HubTicketController : ApiControllerBase
{
    /// <summary>
    /// For Solo's own sign-ins only, a provider token or a session. An
    /// extension's credential has no business on the board hub, and a ticket
    /// can't buy another: it isn't accepted off the hub path.
    /// </summary>
    [HttpPost]
    // A bare string would go out as text/plain; every other response is JSON.
    [Produces("application/json")]
    public ActionResult<string> Create(
        [FromServices] HubTickets tickets,
        [FromServices] CurrentUserAccessor currentUser)
    {
        if (!User.IsSoloSignIn() || currentUser.User is not { } user)
        {
            return Forbid();
        }

        var sessionId = Guid.TryParse(User.FindFirst(AuthConstants.SessionIdClaim)?.Value, out var id)
            ? id
            : (Guid?)null;
        return tickets.Issue(new HubTickets.Holder(user.Issuer, user.Subject, user.Email, user.DisplayName, sessionId));
    }
}
