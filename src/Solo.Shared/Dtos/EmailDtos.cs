using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

/// <summary>
/// The instance's outgoing mail (contract 1.16). The password never comes back:
/// <paramref name="HasPassword"/> says one is stored, and
/// <paramref name="PasswordFromEnvironment"/> that the server's environment
/// supplies one, which wins over the stored one.
/// </summary>
public sealed record EmailSettingsDto(
    bool Enabled,
    string? PublicAddress,
    string? FromName,
    string? FromAddress,
    string? SmtpHost,
    int SmtpPort,
    SmtpSecurity Security,
    string? Username,
    bool HasPassword,
    bool PasswordFromEnvironment,
    IReadOnlyList<EmailFailureDto> RecentFailures,
    uint Version);

/// <summary>An email the outbox gave up on.</summary>
public sealed record EmailFailureDto(string To, string Subject, string? Error, DateTimeOffset FailedAt);

/// <summary>
/// Also the body of a test send, which tries these values before they are saved.
/// <see cref="Password"/>: null keeps the stored one, empty clears it.
/// </summary>
public sealed class UpdateEmailSettingsRequest
{
    public bool Enabled { get; init; }

    [MaxLength(EmailLimits.PublicAddressLength)]
    public string? PublicAddress { get; init; }

    [MaxLength(EmailLimits.NameLength)]
    public string? FromName { get; init; }

    [MaxLength(EmailLimits.AddressLength)]
    public string? FromAddress { get; init; }

    [MaxLength(EmailLimits.HostLength)]
    public string? SmtpHost { get; init; }

    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    public SmtpSecurity Security { get; init; }

    [MaxLength(EmailLimits.NameLength)]
    public string? Username { get; init; }

    [MaxLength(EmailLimits.PasswordLength)]
    public string? Password { get; init; }

    public uint Version { get; init; }
}

public sealed record TestEmailResultDto(string SentTo);

public static class EmailLimits
{
    public const int PublicAddressLength = 2048;
    public const int NameLength = 200;
    /// <summary>RFC 5321's limit on a whole address.</summary>
    public const int AddressLength = 320;
    public const int HostLength = 253;
    public const int PasswordLength = 512;
}
