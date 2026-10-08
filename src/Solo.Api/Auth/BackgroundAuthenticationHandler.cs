using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solo.Data;
using Solo.Extensibility;
using Solo.Extensions;

namespace Solo.Api.Auth;

/// <summary>
/// Authenticates <c>solobg_</c> tokens: an extension's background work calling
/// core as a user who granted it (see <see cref="ExtensionBackground"/>).
/// </summary>
/// <remarks>
/// <para>The token only names a grant. The grant is read again on every request,
/// so one revoked by the user, by the extension, or by a super admin signing the
/// user out everywhere stops working on the next call, and a disabled extension's
/// work stops with it.</para>
///
/// <para>The principal carries <c>iss</c> and <c>sub</c> from the user row and
/// nothing else of theirs, least of all <c>email</c>: provisioning reads that as
/// the provider's current word and would act on it. The extension id goes in
/// <see cref="SoloBackgroundClaims.ExtensionId"/>, from the grant rather than the
/// caller.</para>
///
/// <para>The identity's type is this scheme, which is how
/// <see cref="CurrentUserAccessor.IsSuperAdmin"/> knows to withhold super admin,
/// and why <see cref="CredentialKind"/>'s allowlists already refuse it a session
/// or a hub ticket of its own.</para>
/// </remarks>
public sealed class BackgroundAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BackgroundTokens tokens,
    ExtensionState extensions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = SessionTokens.FromRequest(Request);
        if (!BackgroundTokens.IsBackgroundToken(token))
        {
            return AuthenticateResult.NoResult();
        }

        if (tokens.Read(token) is not { } grantId)
        {
            return AuthenticateResult.Fail("Malformed, forged or expired background token.");
        }

        var db = Context.RequestServices.GetRequiredService<SoloDbContext>();
        var grant = await db.ExtensionGrants.AsNoTracking()
            .Include(g => g.User)
            .FirstOrDefaultAsync(g => g.Id == grantId, Context.RequestAborted);
        if (grant is null)
        {
            return AuthenticateResult.Fail("The background grant this token names has been revoked.");
        }

        if (extensions.IsDisabled(grant.ExtensionId, out _))
        {
            return AuthenticateResult.Fail($"Extension '{grant.ExtensionId}' is disabled.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("iss", grant.User.Issuer),
                new Claim("sub", grant.User.Subject),
                new Claim(SoloBackgroundClaims.ExtensionId, grant.ExtensionId),
            ],
            Scheme.Name, nameType: "name", roleType: "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
