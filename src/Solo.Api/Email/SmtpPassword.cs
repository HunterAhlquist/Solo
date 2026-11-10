using Microsoft.AspNetCore.DataProtection;
using Solo.Data.Entities;

namespace Solo.Api.Email;

/// <summary>
/// Seals the SMTP password for storage, and finds the one to use: the
/// environment's when it sets one, so an operator can keep it out of the
/// database entirely.
/// </summary>
public sealed class SmtpPassword(IDataProtectionProvider protection, IConfiguration config)
{
    public const string EnvironmentKey = "Email:Smtp:Password";

    private readonly IDataProtector _protector = protection.CreateProtector("Solo.Email.SmtpPassword");

    public bool FromEnvironment => !string.IsNullOrEmpty(config[EnvironmentKey]);

    public string Protect(string password) => _protector.Protect(password);

    public string? Resolve(EmailSettings settings) =>
        FromEnvironment ? config[EnvironmentKey]
        : settings.ProtectedPassword is { } sealedPassword ? _protector.Unprotect(sealedPassword)
        : null;
}
