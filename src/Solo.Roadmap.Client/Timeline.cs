using Solo.Roadmap.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Roadmap.Client;

/// <summary>
/// The timeline's rules, in days: where a task's bar runs, which stripe a day
/// falls in, where the view opens, what counts as missed. Kept apart from the
/// page so each rule is a plain function a test can pin down.
/// </summary>
public static class Timeline
{
    /// <summary>Rows the grid shows even when the roadmap has fewer, so an empty one still has a surface to click.</summary>
    public const int MinimumRows = 9;

    /// <summary>Empty rows kept below the last task, however many there are: where a new one is added.</summary>
    public const int SpareRows = 2;

    public static int Rows(int entries) => Math.Max(MinimumRows, entries + SpareRows);

    /// <summary>
    /// The dates a task with none gets when it's added on <paramref name="day"/>:
    /// that day alone, or — on a day already past — from it through today, so it
    /// doesn't arrive already missed.
    /// </summary>
    public static DaySpan ScheduleOnAdd(DateOnly day, DateOnly today) => new(day, day < today ? today : day);

    /// <summary>A task's start: its own, or the day it was created — the same fallback its board card uses.</summary>
    public static DateOnly EffectiveStart(TaskDto task) =>
        task.StartAt ?? DateOnly.FromDateTime(task.CreatedAt.ToLocalTime());

    /// <summary>
    /// The days a row covers, inclusive. A task with no deadline is a one-day pill
    /// on the day it was added; one created after its own deadline starts on it.
    /// </summary>
    public static DaySpan Span(TaskDto task, DateOnly anchorDay)
    {
        if (task.DeadlineAt is not { } end)
        {
            return new DaySpan(anchorDay, anchorDay);
        }

        var start = EffectiveStart(task);
        return new DaySpan(start <= end ? start : end, end);
    }

    /// <summary>
    /// Which stripe a day falls in, counted continuously from the anchor so the
    /// pattern never restarts at a year boundary. Floor division: the day before
    /// the anchor is in band -1, not band 0.
    /// </summary>
    public static int Band(DateOnly day, DateOnly anchor, int intervalDays) =>
        FloorDiv(day.DayNumber - anchor.DayNumber, intervalDays);

    /// <summary>Even bands are light, odd bands dark.</summary>
    public static bool IsDarkBand(DateOnly day, DateOnly anchor, int intervalDays) =>
        (Band(day, anchor, intervalDays) & 1) == 1;

    public static int FloorDiv(int dividend, int divisor)
    {
        var quotient = dividend / divisor;
        return dividend % divisor != 0 && (dividend < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }

    /// <summary>
    /// The day the view opens on: of the rows whose range includes today, the
    /// one that started longest ago — so the work in flight is all to its right
    /// — or today itself when nothing is in flight.
    /// </summary>
    public static DateOnly StartingDay(IEnumerable<DaySpan> spans, DateOnly today) =>
        spans.Where(s => s.Start <= today && today <= s.End)
            .Select(s => (DateOnly?)s.Start)
            .Min() ?? today;

    /// <summary>The board's rule: the deadline day has ended and the task isn't finished.</summary>
    public static bool IsMissed(TaskDto task, DateOnly today) =>
        task.DeadlineAt is { } deadline && deadline < today && task.Status != TaskState.Finished;

    /// <summary>
    /// A milestone flashes while it's due: from the start of the sprint it falls
    /// in (or the one before, see <see cref="RoadmapSprints.MilestoneDueFrom"/>)
    /// through its own day. Once that day has ended it's history, and stops.
    /// </summary>
    public static bool IsMilestoneDue(DateOnly day, DateOnly today, RoadmapSettingsDto settings) =>
        today <= day && today >= RoadmapSprints.MilestoneDueFrom(day, settings.SprintStartDay, settings.SprintLengthWeeks);

    public static int DaysOverdue(TaskDto task, DateOnly today) =>
        today.DayNumber - task.DeadlineAt!.Value.DayNumber;

    /// <summary>
    /// Finished, and its last day already behind today: history, which the edge
    /// hints don't point back to once its bar is scrolled out of view.
    /// </summary>
    public static bool IsDone(TaskDto task, DaySpan span, DateOnly today) =>
        task.Status == TaskState.Finished && span.End < today;

    /// <summary>
    /// The dates to save after an edge of a row is dragged to <paramref name="day"/>.
    /// The start can't pass the deadline, so a bar is always at least a day long.
    /// </summary>
    /// <remarks>
    /// Dragging the left edge gives the task an explicit start even when it was
    /// drawn from its creation date. A no-deadline pill gains a deadline either
    /// way: dragging its right edge keeps the start on the pill's day, dragging
    /// its left edge makes the pill's day the deadline.
    /// </remarks>
    public static (DateOnly? Start, DateOnly Deadline) Resize(TaskDto task, DateOnly anchorDay, Edge edge, DateOnly day)
    {
        var span = Span(task, anchorDay);
        var pill = task.DeadlineAt is null;
        return edge switch
        {
            Edge.End when pill => (anchorDay, Max(day, anchorDay)),
            Edge.End => (task.StartAt, Max(day, span.Start)),
            _ when pill => (Min(day, anchorDay), anchorDay),
            _ => (Min(day, span.End), span.End),
        };
    }

    /// <summary>A row's days moved <paramref name="days"/> along the timeline, its length unchanged.</summary>
    public static DaySpan Move(DaySpan span, int days) => new(span.Start.AddDays(days), span.End.AddDays(days));

    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;

    private static DateOnly Max(DateOnly a, DateOnly b) => a >= b ? a : b;
}

public enum Edge
{
    Start,
    End,
}

/// <summary>An inclusive run of days.</summary>
public readonly record struct DaySpan(DateOnly Start, DateOnly End);

/// <summary>
/// The scrollable window of days: from <c>today − past</c> to <c>today + future</c>,
/// and the arithmetic between days, column indexes and pixels.
/// </summary>
public sealed record TimelineWindow(DateOnly First, DateOnly Last)
{
    public static TimelineWindow Around(DateOnly today, RoadmapSettingsDto settings) =>
        new(today.AddDays(-settings.PastDays), today.AddDays(settings.FutureDays));

    public int Days => Last.DayNumber - First.DayNumber + 1;

    public int IndexOf(DateOnly day) => day.DayNumber - First.DayNumber;

    public DateOnly DayAt(int index) => First.AddDays(index);

    public DateOnly Clamp(DateOnly day) => day < First ? First : day > Last ? Last : day;

    /// <summary>
    /// How far a row may move by up to <paramref name="days"/> and stay inside
    /// the window — or not move at all, if it's already past an edge.
    /// </summary>
    public int ClampMove(DaySpan span, int days) =>
        days < 0 ? Math.Min(Math.Max(days, First.DayNumber - span.Start.DayNumber), 0)
                 : Math.Max(Math.Min(days, Last.DayNumber - span.End.DayNumber), 0);

    /// <summary>
    /// Where a row's bar sits, in column indexes, clipped to the window. A span
    /// wholly outside it keeps a one-day stub at the edge it's beyond, so the row
    /// still says where its work went.
    /// </summary>
    public BarPlacement Place(DaySpan span)
    {
        var start = IndexOf(span.Start);
        var end = IndexOf(span.End);
        return new BarPlacement(
            Math.Clamp(start, 0, Days - 1),
            Math.Clamp(end, 0, Days - 1),
            ClippedStart: start < 0,
            ClippedEnd: end > Days - 1);
    }

    /// <summary>
    /// The background offset that lines a two-band stripe pattern up with the
    /// anchor, however far outside the window the anchor falls.
    /// </summary>
    public double StripeOffset(DateOnly anchor, int intervalDays, double dayWidth)
    {
        var period = 2 * intervalDays * dayWidth;
        var x = IndexOf(anchor) * dayWidth;
        return (x % period + period) % period;
    }
}

public readonly record struct BarPlacement(int StartIndex, int EndIndex, bool ClippedStart, bool ClippedEnd)
{
    public int Length => EndIndex - StartIndex + 1;

    public bool Overlaps(int firstIndex, int lastIndex) => StartIndex <= lastIndex && EndIndex >= firstIndex;
}

/// <summary>
/// The columns worth rendering for a scroll position: what's on screen plus a
/// buffer either side, re-cut only when the view nears the edge of what was
/// last rendered — so scrolling costs a render every few weeks of days, not
/// every frame.
/// </summary>
public static class Viewport
{
    public const int Buffer = 45;

    /// <summary>How close to the rendered edge the view may get before it's re-cut.</summary>
    public const int Margin = 15;

    public static (int First, int Last) Visible(double scrollLeft, double width, double dayWidth, int days) =>
        (Math.Clamp((int)Math.Floor(scrollLeft / dayWidth), 0, Math.Max(days - 1, 0)),
         Math.Clamp((int)Math.Ceiling((scrollLeft + width) / dayWidth), 0, Math.Max(days - 1, 0)));

    public static (int First, int Last) Rendered((int First, int Last) visible, int days) =>
        (Math.Max(visible.First - Buffer, 0), Math.Min(visible.Last + Buffer, days - 1));

    /// <summary>True when the visible range has come within the margin of the rendered one's edge.</summary>
    public static bool NeedsRecut((int First, int Last) visible, (int First, int Last) rendered, int days) =>
        (visible.First - Margin < rendered.First && rendered.First > 0) ||
        (visible.Last + Margin > rendered.Last && rendered.Last < days - 1);
}
