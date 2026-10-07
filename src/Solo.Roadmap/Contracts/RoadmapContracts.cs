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

    public const int MilestoneName = 100;

    /// <summary>A milestone's colour when nobody picks one: a red that stands out from the grid in either scheme.</summary>
    public const string DefaultMilestoneColor = "#e53935";

    /// <summary>The only colour form accepted — what a CSS custom property can take as it is.</summary>
    public const string ColorPattern = "^#[0-9a-fA-F]{6}$";

    public const int MinBoundDays = 1;
    public const int MaxBoundDays = 3650;

    public const int MinSprintWeeks = 1;
    public const int MaxSprintWeeks = 52;

    /// <summary>Entries one request removes or moves together; the page splits a larger selection.</summary>
    public const int MaxEntriesAtOnce = 500;

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

    /// <summary>
    /// A milestone on one of its sprint's first this-many days counts as due in
    /// the sprint before: a deadline on a sprint's second day is work for the
    /// sprint ending just ahead of it, not for the one it barely starts.
    /// </summary>
    public const int MilestoneLeadDays = 3;

    /// <summary>
    /// The first day a milestone on <paramref name="day"/> is due from: the start
    /// of its sprint, or of the sprint before when it falls on one of its
    /// sprint's first <see cref="MilestoneLeadDays"/> days.
    /// </summary>
    public static DateOnly MilestoneDueFrom(DateOnly day, DayOfWeek startDay, int weeks)
    {
        var start = SprintStart(day, startDay, weeks);
        return day.DayNumber - start.DayNumber < MilestoneLeadDays ? start.AddDays(-weeks * 7) : start;
    }
}

/// <summary>
/// The live topics this extension publishes on (see <c>ISoloLiveUpdates</c>),
/// and what each message means. Messages say what changed, not what it became:
/// a page that hears one refetches through the API.
/// </summary>
public static class RoadmapTopics
{
    /// <summary>A project's roadmaps were created, renamed or deleted — for lists of them.</summary>
    public const string List = "roadmap:list";

    /// <summary>One roadmap: its title, its entries, and the tasks on it.</summary>
    public static string Roadmap(Guid roadmapId) => $"roadmap:{roadmapId:N}";

    /// <summary>On <see cref="List"/>: the project's roadmaps changed.</summary>
    public const string ListChanged = "changed";

    /// <summary>Entries were added, removed, reordered or moved.</summary>
    public const string EntriesChanged = "entries";

    /// <summary>A milestone was added, edited, moved or deleted.</summary>
    public const string MilestonesChanged = "milestones";

    /// <summary>A task on the roadmap changed; the data is its id.</summary>
    public const string TaskChanged = "task";

    public const string Renamed = "renamed";

    public const string Deleted = "deleted";
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
    IReadOnlyList<RoadmapEntryDto> Entries,
    IReadOnlyList<RoadmapMilestoneDto> Milestones);

/// <summary>
/// A named deadline on a roadmap, drawn as a line at the end of its
/// <see cref="Day"/>. It belongs to the roadmap alone; no task's dates follow it.
/// </summary>
public sealed record RoadmapMilestoneDto(Guid Id, string Name, DateOnly Day, string Color, uint Version);

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

public sealed class CreateRoadmapMilestoneRequest
{
    [Required, MaxLength(RoadmapLimits.MilestoneName)]
    public required string Name { get; init; }

    public DateOnly Day { get; init; }

    [Required, RegularExpression(RoadmapLimits.ColorPattern)]
    public string Color { get; init; } = RoadmapLimits.DefaultMilestoneColor;
}

/// <summary>Everything about a milestone at once, from its dialog or a drag of its line.</summary>
public sealed class UpdateRoadmapMilestoneRequest
{
    [Required, MaxLength(RoadmapLimits.MilestoneName)]
    public required string Name { get; init; }

    public DateOnly Day { get; init; }

    [Required, RegularExpression(RoadmapLimits.ColorPattern)]
    public required string Color { get; init; }

    public uint Version { get; init; }
}

/// <summary>Entries to take off a roadmap together, such as everything selected on the page.</summary>
public sealed class RemoveRoadmapEntriesRequest
{
    [Required, MinLength(1), MaxLength(RoadmapLimits.MaxEntriesAtOnce)]
    public required IReadOnlyList<Guid> EntryIds { get; init; }
}

/// <summary>Neighbors in roadmap order, like core's rerank. Both null moves to the top.</summary>
public sealed class RerankRoadmapEntryRequest
{
    public Guid? PrecedingId { get; init; }

    public Guid? FollowingId { get; init; }
}

/// <summary>
/// Several rows moved together, as a block, between neighbors that aren't
/// among them. The rows keep the order they had relative to one another.
/// </summary>
public sealed class RerankRoadmapEntriesRequest
{
    [Required, MinLength(1), MaxLength(RoadmapLimits.MaxEntriesAtOnce)]
    public required IReadOnlyList<Guid> EntryIds { get; init; }

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
