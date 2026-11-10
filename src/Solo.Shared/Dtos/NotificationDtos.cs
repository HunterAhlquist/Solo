using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

/// <summary>
/// One entry in the signed-in person's inbox (contract 1.16). The text is
/// written when the notification is raised, so it still reads after its task
/// or epic is deleted; the ids are null then.
/// </summary>
public sealed record NotificationDto(
    Guid Id,
    NotificationKind Kind,
    string Title,
    string? Body,
    Guid? ProjectId,
    Guid? BoardId,
    Guid? TaskId,
    Guid? EpicId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>The newest notifications, and how many of all of them are unread.</summary>
public sealed record NotificationInboxDto(IReadOnlyList<NotificationDto> Items, int UnreadCount);

/// <summary>
/// <paramref name="TimeZone"/> is an IANA id; null until a client reports one,
/// and deadline reminders go by UTC meanwhile.
/// </summary>
public sealed record NotificationPreferencesDto(
    string? TimeZone,
    bool NotifyOnAssigned,
    bool NotifyOnDeadline,
    uint Version);

public sealed class UpdateNotificationPreferencesRequest
{
    [MaxLength(NotificationLimits.TimeZoneLength)]
    public string? TimeZone { get; init; }

    public bool NotifyOnAssigned { get; init; }

    public bool NotifyOnDeadline { get; init; }

    public uint Version { get; init; }
}

public static class NotificationLimits
{
    /// <summary>The longest IANA id is about 30 characters; this leaves room without inviting a payload.</summary>
    public const int TimeZoneLength = 64;

    public const int TitleLength = 500;

    public const int BodyLength = 1000;

    /// <summary>How many the inbox returns; older ones stay until the daily sweep removes them.</summary>
    public const int InboxPageSize = 50;
}
