using System.ComponentModel.DataAnnotations;
using Solo.Shared.Dtos;

namespace Solo.Roadmap.Contracts;

// ---------------------------------------------------------------------------
// The extension's API shapes. This one file is compiled into both halves (the
// client project links it), so the package agrees with itself by construction
// and core carries none of it. Only BCL and Solo.Shared types appear here —
// the two things both halves can reference.
// ---------------------------------------------------------------------------

public static class RoadmapLimits
{
    public const int Title = 300;

    public const int MinBoundDays = 1;
    public const int MaxBoundDays = 3650;

    public const int MinStripeIntervalDays = 1;
    public const int MaxStripeIntervalDays = 365;

    public const int DefaultPastDays = 365;
    public const int DefaultFutureDays = 730;
    public const int DefaultStripeIntervalDays = 14;
}

/// <summary>A sidebar row.</summary>
public sealed record RoadmapSummaryDto(Guid Id, string Title, uint Version);

/// <summary>Everything the timeline page draws, in one read.</summary>
public sealed record RoadmapTimelineDto(
    Guid Id,
    Guid ProjectId,
    string ProjectAlias,
    string Title,
    uint Version,
    RoadmapSettingsDto Settings,
    IReadOnlyList<RoadmapEntryDto> Entries);

/// <summary>
/// One task on a roadmap, in roadmap rank order. <see cref="AnchorDay"/> is the
/// day it was added on — where a task with no deadline is drawn.
/// </summary>
public sealed record RoadmapEntryDto(
    Guid Id,
    decimal Rank,
    DateOnly AnchorDay,
    string BoardAlias,
    string BoardName,
    TaskDto Task);

/// <summary>A task the picker offers: one not on this roadmap yet, with the board it lives on.</summary>
public sealed record RoadmapCandidateDto(TaskDto Task, Guid BoardId, string BoardName, string BoardAlias);

/// <summary>A roadmap a task appears on, for the chips in its task view.</summary>
public sealed record TaskRoadmapDto(Guid Id, string Title, string ProjectAlias);

/// <summary>
/// The timeline's instance-wide settings. <see cref="StripeAnchor"/> is the
/// resolved date the stripes count from; <see cref="StripeStartDayOfYear"/> is
/// that date's day of year, which is what the admin edits.
/// </summary>
public sealed record RoadmapSettingsDto(
    int PastDays,
    int FutureDays,
    int StripeStartDayOfYear,
    DateOnly StripeAnchor,
    int StripeIntervalDays,
    uint Version);

public sealed class CreateRoadmapRequest
{
    [Required, MaxLength(RoadmapLimits.Title)]
    public required string Title { get; init; }
}

public sealed class RenameRoadmapRequest
{
    [Required, MaxLength(RoadmapLimits.Title)]
    public required string Title { get; init; }

    public uint Version { get; init; }
}

public sealed class AddRoadmapEntryRequest
{
    public Guid TaskId { get; init; }

    /// <summary>The day the task was added on; a task with no deadline is drawn here.</summary>
    public DateOnly AnchorDay { get; init; }
}

/// <summary>Neighbors in roadmap order, like core's rerank. Both null moves to the top.</summary>
public sealed class RerankRoadmapEntryRequest
{
    public Guid? PrecedingId { get; init; }

    public Guid? FollowingId { get; init; }
}

public sealed class UpdateRoadmapSettingsRequest
{
    [Range(RoadmapLimits.MinBoundDays, RoadmapLimits.MaxBoundDays)]
    public int PastDays { get; init; }

    [Range(RoadmapLimits.MinBoundDays, RoadmapLimits.MaxBoundDays)]
    public int FutureDays { get; init; }

    /// <summary>366 only in a leap year: the day resolves to a date in the year it's saved.</summary>
    [Range(1, 366)]
    public int StripeStartDayOfYear { get; init; }

    [Range(RoadmapLimits.MinStripeIntervalDays, RoadmapLimits.MaxStripeIntervalDays)]
    public int StripeIntervalDays { get; init; }

    public uint Version { get; init; }
}
