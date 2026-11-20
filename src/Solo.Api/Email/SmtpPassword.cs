using Microsoft.AspNetCore.DataProtection;
using Solo.Data.Entities;

namespace Solo.Api.Email;

/// <summary>
/// Seals the SMTP password for storage, and finds the one to use: the
/// environment's when it sets one, so an operator can keep it out of the
/// database entirely.
/// </summary>
/// <remarks>
/// A password only ever goes to the server it was given for. A stored one is
/// dropped when the settings are pointed somewhere else
/// (<c>EmailSettingsController</c>). The environment's can't be dropped, and
/// the settings are a super admin's to change, who need not be the operator
/// holding the password: pointed at a server of their own, the settings would
/// hand it to them. So the environment names its server as well
/// (<see cref="EnvironmentHostKey"/>), and the password goes nowhere else. Set
/// without one, it is not used at all.
/// </remarks>
public sealed class SmtpPassword(IDataProtectionProvider protection, IConfiguration config)
{
    public const string EnvironmentKey = "Email:Smtp:Password";

    /// <summary>The server the environment's password is for.</summary>
    public const string EnvironmentHostKey = "Email:Smtp:Host";

    private readonly IDataProtector _protector = protection.CreateProtector("Solo.Email.SmtpPassword");

    public bool FromEnvironment => !string.IsNullOrEmpty(config[EnvironmentKey]);

    public string Protect(string password) => _protector.Protect(password);

    public string? EnvironmentHost =>
        config[EnvironmentHostKey] is { } host && !string.IsNullOrWhiteSpace(host) ? host.Trim() : null;

    /// <summary>
    /// Why the environment's password may not be used with this SMTP server,
    /// in words for whoever is setting email up. Null when it may, and when
    /// the environment sets no password.
    /// </summary>
    public string? Refusal(string? smtpHost)
    {
        if (!FromEnvironment)
        {
            return null;
        }

        if (EnvironmentHost is not { } allowed)
        {
            return "The SMTP password is set in the server's environment, which must also name the server it is for. " +
                   "Set Email__Smtp__Host there.";
        }

        return string.Equals(allowed, smtpHost?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The SMTP password in the server's environment is for {allowed}. Use that server, or change Email__Smtp__Host.";
    }

    public string? Resolve(EmailSettings settings) =>
        FromEnvironment ? (Refusal(settings.SmtpHost) is null ? config[EnvironmentKey] : null)
        : settings.ProtectedPassword is { } sealedPassword ? _protector.Unprotect(sealedPassword)
        : null;
}
