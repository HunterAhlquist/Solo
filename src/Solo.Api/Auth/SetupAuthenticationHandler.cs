using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Auth;

/// <summary>
/// Lightweight setup-only auth scheme for the static super admin account used
/// during first-run setup. Credentials come from environment config
/// (Setup:Username / Setup:Password) and arrive as
/// <c>Authorization: Setup base64(username:password)</c>. The scheme is
/// permanently disabled once <see cref="InstanceSettings.SetupCompleted"/> is
/// true, and never enabled with a guessable password (see <see cref="SetupAccount"/>).
/// </summary>
public sealed class SetupAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SetupAccount account,
    SoloDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// Set when this request was refused because the account is disabled rather
    /// than because the pair was wrong. The challenge reports it.
    /// </summary>
    private string? _disabledReason;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith($"{AuthConstants.SetupScheme} ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[(AuthConstants.SetupScheme.Length + 1)..].Trim()));
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Malformed setup credentials.");
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return AuthenticateResult.Fail("Malformed setup credentials.");
        }

        var settings = await db.InstanceSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == InstanceSettings.SingletonId);
        if (settings?.SetupCompleted == true)
        {
            return AuthenticateResult.Fail("Setup is complete; the static setup account is disabled.");
        }

        if (account.DisabledReason is { } reason)
        {
            _disabledReason = reason;
            return AuthenticateResult.Fail(reason);
        }

        if (!account.Accepts(decoded[..separator], decoded[(separator + 1)..]))
        {
            return AuthenticateResult.Fail("Invalid setup credentials.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "setup"),
                new Claim(ClaimTypes.Role, AuthConstants.SetupAdminRole),
            ],
            AuthConstants.SetupScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), AuthConstants.SetupScheme);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// A disabled account says why, so the wizard can show it: the operator is
    /// typing the password they configured, and a bare 401 tells them it's wrong.
    /// Only reached while setup is pending (the completed check comes first), so
    /// a finished instance never describes its setup password to anyone.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (_disabledReason is null)
        {
            await base.HandleChallengeAsync(properties);
            return;
        }

        await Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "The setup account is disabled",
            detail: _disabledReason).ExecuteAsync(Context);
    }
}
