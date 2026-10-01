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

    public const int MinSprintWeeks = 1;
    public const int MaxSprintWeeks = 52;

    public const int DefaultPastDays = 365;
    public const int DefaultFutureDays = 730;
    public const DayOfWeek DefaultSprintStartDay = DayOfWeek.Monday;
    public const int DefaultSprintWeeks = 2;
}

/// <summary>
/// Where the sprint stripes fall, worked out from the settings alone: the bands
/// count from a fixed reference, the first sprint start day on or after
/// 1 January of year 1, so the pattern is the same on every instance and runs
/// back into the past unchanged — nothing about when the settings were saved
/// moves it.
/// </summary>
public static class RoadmapSprints
{
    /// <summary>A day that starts a sprint; every sprint starts a whole number of sprint lengths from it.</summary>
    public static DateOnly Anchor(DayOfWeek startDay) =>
        DateOnly.MinValue.AddDays(((int)startDay - (int)DateOnly.MinValue.DayOfWeek + 7) % 7);

    /// <summary>The first day of the sprint <paramref name="day"/> falls in.</summary>
    public static DateOnly SprintStart(DateOnly day, DayOfWeek startDay, int weeks)
    {
        var length = weeks * 7;
        var anchor = Anchor(startDay);
        var offset = day.DayNumber - anchor.DayNumber;
        return anchor.AddDays(offset - (((offset % length) + length) % length));
    }
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
/// The timeline's instance-wide settings: how far it scrolls each way, and the
/// sprint indication — the day of the week sprints start on and how many weeks
/// each lasts. <see cref="RoadmapSprints"/> turns the last two into stripes.
/// </summary>
public sealed record RoadmapSettingsDto(
    int PastDays,
    int FutureDays,
    DayOfWeek SprintStartDay,
    int SprintLengthWeeks,
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

/// <summary>The day a task with no deadline is drawn on, when its pill is dragged along the timeline.</summary>
public sealed class SetRoadmapEntryAnchorRequest
{
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

    [EnumDataType(typeof(DayOfWeek))]
    public DayOfWeek SprintStartDay { get; init; }

    [Range(RoadmapLimits.MinSprintWeeks, RoadmapLimits.MaxSprintWeeks)]
    public int SprintLengthWeeks { get; init; }

    public uint Version { get; init; }
}
