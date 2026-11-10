using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>
/// The instance's outgoing mail. One row, made on the first save; until then
/// email is off. Kept out of <see cref="InstanceSettings"/> and the audit log so
/// the password's ciphertext isn't copied into history on every change.
/// </summary>
public class EmailSettings : IVersioned, INotAudited
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool Enabled { get; set; }
    /// <summary>
    /// Where people reach the instance; every link in an email is built on it.
    /// Requests can't tell us, since the desktop app's arrive as localhost.
    /// </summary>
    public string? PublicAddress { get; set; }
    public string? FromName { get; set; }
    public string? FromAddress { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity Security { get; set; }
    public string? Username { get; set; }
    /// <summary>Encrypted with ASP.NET Core Data Protection, whose keys live in <see cref="DataProtectionKey"/>.</summary>
    public string? ProtectedPassword { get; set; }
    public uint Version { get; set; }
}

/// <summary>
/// One email waiting to go, sent, or given up on. Every email is a row first,
/// so a restart or an SMTP outage loses nothing.
/// </summary>
public class EmailMessage : INotAudited
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string To { get; set; }
    public required string Subject { get; set; }
    public required string HtmlBody { get; set; }
    public required string TextBody { get; set; }
    /// <summary>"invite", "digest" and the like, for the logs.</summary>
    public required string Kind { get; set; }
    /// <summary>A one-click unsubscribe URL for the List-Unsubscribe header, where the email has one.</summary>
    public string? UnsubscribeUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int Attempts { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// A Data Protection key, kept in Postgres so encrypted settings outlive the
/// container: the default key folder is inside it.
/// </summary>
public class DataProtectionKey : INotAudited
{
    public int Id { get; set; }
    public string? FriendlyName { get; set; }
    public required string Xml { get; set; }
}
