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
    DateTimeOffset? ReadAt)
{
    /// <summary>
    /// A deadline entry's reminder key (contract 1.17): the
    /// <see cref="UpcomingReminderDto.Key"/> it was announced under before the
    /// server raised it, so a device that scheduled that reminder knows this
    /// entry is the same news. Null for other kinds.
    /// </summary>
    public string? Key { get; init; }
}

/// <summary>The newest notifications, and how many of all of them are unread.</summary>
public sealed record NotificationInboxDto(IReadOnlyList<NotificationDto> Items, int UnreadCount);

/// <summary>
/// <paramref name="TimeZone"/> is an IANA id; null until a client reports one,
/// and reminders and digests go by UTC meanwhile.
/// </summary>
public sealed record NotificationPreferencesDto(
    string? TimeZone,
    bool NotifyOnAssigned,
    bool NotifyOnDeadline,
    bool EmailOnAssigned,
    bool EmailOnDeadline,
    DigestFrequency DigestFrequency,
    int DigestHour,
    DayOfWeek DigestWeekday,
    uint Version);

public sealed class UpdateNotificationPreferencesRequest
{
    [MaxLength(NotificationLimits.TimeZoneLength)]
    public string? TimeZone { get; init; }

    public bool NotifyOnAssigned { get; init; }

    public bool NotifyOnDeadline { get; init; }

    public bool EmailOnAssigned { get; init; }

    public bool EmailOnDeadline { get; init; }

    public DigestFrequency DigestFrequency { get; init; }

    [Range(0, 23)]
    public int DigestHour { get; init; }

    public DayOfWeek DigestWeekday { get; init; }

    public uint Version { get; init; }
}

/// <summary>
/// A deadline reminder the server will raise later, for a device to schedule
/// ahead so it shows while the app is closed (contract 1.17). <paramref name="Key"/>
/// is the notification's dedupe key, the same one the reminder is raised under,
/// and <paramref name="Title"/> and <paramref name="Body"/> its text as it will
/// read when <paramref name="FireAt"/> comes.
/// </summary>
public sealed record UpcomingReminderDto(
    string Key,
    NotificationKind Kind,
    Guid TaskId,
    DateTimeOffset FireAt,
    string Title,
    string? Body);

public static class NotificationLimits
{
    /// <summary>The most upcoming reminders one call returns, soonest first; iOS keeps 64 pending at most anyway.</summary>
    public const int UpcomingLimit = 100;

    /// <summary>The longest IANA id is about 30 characters; this leaves room without inviting a payload.</summary>
    public const int TimeZoneLength = 64;

    public const int TitleLength = 500;

    public const int BodyLength = 1000;

    /// <summary>How many the inbox returns; older ones stay until the daily sweep removes them.</summary>
    public const int InboxPageSize = 50;
}
