using System.Security.Claims;
using System.Security.Cryptography;
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
/// permanently disabled once <see cref="InstanceSettings.SetupCompleted"/> is true.
/// </summary>
public sealed class SetupAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration config,
    SoloDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
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

        if (!CredentialsValid(decoded[..separator], decoded[(separator + 1)..], config))
        {
            return AuthenticateResult.Fail("Invalid setup credentials.");
        }

        var settings = await db.InstanceSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == InstanceSettings.SingletonId);
        if (settings?.SetupCompleted == true)
        {
            return AuthenticateResult.Fail("Setup is complete; the static setup account is disabled.");
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

    public static bool CredentialsValid(string username, string password, IConfiguration config)
    {
        var expectedUser = config["Setup:Username"];
        var expectedPassword = config["Setup:Password"];
        if (string.IsNullOrEmpty(expectedUser) || string.IsNullOrEmpty(expectedPassword))
        {
            return false;
        }

        return FixedTimeEquals(username, expectedUser) && FixedTimeEquals(password, expectedPassword);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
