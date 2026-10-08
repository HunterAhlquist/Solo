using Solo.Roadmap.Client;
using Solo.Roadmap.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Roadmap.Tests;

/// <summary>The timeline's rules as plain functions: stripes, where the view opens, bars, resizing, moving.</summary>
public class TimelineTests
{
    private static readonly DateOnly Anchor = new(2026, 1, 5);
    private static readonly DateOnly Today = new(2026, 9, 30);

    private static TaskDto Task(DateOnly? start = null, DateOnly? deadline = null, DateTime? created = null,
        TaskState status = TaskState.Unstarted) =>
        TestData.Task(status: status, createdAt: created ?? new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            deadline: deadline, start: start);

    // -- Stripes -------------------------------------------------------------

    [Theory]
    [InlineData("2026-01-05", 0)]  // the anchor starts band 0
    [InlineData("2026-01-18", 0)]  // the last day of band 0
    [InlineData("2026-01-19", 1)]  // the first day of band 1
    [InlineData("2026-02-01", 1)]
    [InlineData("2026-02-02", 2)]
    [InlineData("2026-01-04", -1)] // the day before the anchor is band -1, not band 0
    [InlineData("2025-12-22", -1)] // the first day of band -1
    [InlineData("2025-12-21", -2)]
    public void A_day_falls_in_the_band_counted_from_the_anchor(string day, int band) =>
        Assert.Equal(band, Timeline.Band(DateOnly.Parse(day), Anchor, 14));

    [Fact]
    public void Even_bands_are_light_and_odd_bands_dark_on_both_sides_of_the_anchor()
    {
        Assert.False(Timeline.IsDarkBand(Anchor, Anchor, 14));
        Assert.True(Timeline.IsDarkBand(Anchor.AddDays(14), Anchor, 14));
        Assert.True(Timeline.IsDarkBand(Anchor.AddDays(-1), Anchor, 14));
        Assert.False(Timeline.IsDarkBand(Anchor.AddDays(-15), Anchor, 14));
    }

    /// <summary>The pattern never restarts at New Year: it counts straight through.</summary>
    [Fact]
    public void Bands_run_continuously_across_a_year_boundary()
    {
        var bands = Enumerable.Range(0, 60).Select(i => Timeline.Band(new DateOnly(2026, 12, 1).AddDays(i), Anchor, 14))
            .Distinct().ToList();

        Assert.Equal(Enumerable.Range(bands[0], bands.Count), bands);
    }

    /// <summary>The CSS background is one repeating layer offset so its band edges fall on the anchor's.</summary>
    [Fact]
    public void The_stripe_layer_is_offset_to_line_up_with_the_anchor()
    {
        var window = new TimelineWindow(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));

        Assert.Equal(4 * 40, window.StripeOffset(Anchor, 14, 40));
        // An anchor before the window still lines up — offset taken modulo the period.
        var later = new TimelineWindow(new DateOnly(2026, 1, 10), new DateOnly(2027, 1, 1));
        var offset = later.StripeOffset(Anchor, 14, 40);
        Assert.InRange(offset, 0, 28 * 40);
        Assert.Equal(0, (later.IndexOf(Anchor) * 40 - offset) % (28 * 40));
    }

    // -- Where the view opens ------------------------------------------------------

    [Fact]
    public void The_view_opens_on_the_oldest_start_among_rows_running_through_today()
    {
        DaySpan[] spans =
        [
            new(Today.AddDays(-3), Today.AddDays(5)),
            new(Today.AddDays(-10), Today.AddDays(1)),
            new(Today.AddDays(-40), Today.AddDays(-2)), // older, but finished before today
            new(Today.AddDays(2), Today.AddDays(9)),    // hasn't started
        ];

        Assert.Equal(Today.AddDays(-10), Timeline.StartingDay(spans, Today));
    }

    [Fact]
    public void With_nothing_in_flight_today_the_view_opens_on_today()
    {
        DaySpan[] spans = [new(Today.AddDays(-9), Today.AddDays(-1)), new(Today.AddDays(1), Today.AddDays(3))];

        Assert.Equal(Today, Timeline.StartingDay(spans, Today));
        Assert.Equal(Today, Timeline.StartingDay([], Today));
    }

    [Fact]
    public void A_range_that_starts_or_ends_today_counts_as_in_flight()
    {
        Assert.Equal(Today, Timeline.StartingDay([new(Today, Today.AddDays(4))], Today));
        Assert.Equal(Today.AddDays(-2), Timeline.StartingDay([new(Today.AddDays(-2), Today)], Today));
    }

    // -- Bars ----------------------------------------------------------------------

    [Fact]
    public void A_bar_runs_from_its_start_to_its_deadline()
    {
        var span = Timeline.Span(Task(start: Today.AddDays(-2), deadline: Today.AddDays(3)), Today);

        Assert.Equal(new DaySpan(Today.AddDays(-2), Today.AddDays(3)), span);
    }

    [Fact]
    public void Without_a_start_a_bar_runs_from_the_day_the_task_was_created()
    {
        var span = Timeline.Span(Task(deadline: Today.AddDays(3)), Today);

        Assert.Equal(new DateOnly(2026, 9, 1), span.Start);
    }

    [Fact]
    public void A_task_with_no_deadline_is_a_one_day_pill_on_the_day_it_was_added()
    {
        var anchor = Today.AddDays(6);

        Assert.Equal(new DaySpan(anchor, anchor), Timeline.Span(Task(), anchor));
    }

    [Fact]
    public void A_task_created_after_its_own_deadline_is_drawn_on_the_deadline()
    {
        var created = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var span = Timeline.Span(Task(deadline: new DateOnly(2026, 9, 10), created: created), Today);

        Assert.Equal(new DaySpan(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 10)), span);
    }

    [Fact]
    public void A_bar_past_the_window_is_clipped_at_its_edge_and_says_so()
    {
        var window = new TimelineWindow(Today.AddDays(-10), Today.AddDays(10));

        var both = window.Place(new DaySpan(Today.AddDays(-20), Today.AddDays(30)));
        Assert.Equal((0, 20, true, true), (both.StartIndex, both.EndIndex, both.ClippedStart, both.ClippedEnd));

        // Wholly before the window: a one-day stub at the edge it's beyond.
        var before = window.Place(new DaySpan(Today.AddDays(-40), Today.AddDays(-30)));
        Assert.Equal((0, 0, true, false), (before.StartIndex, before.EndIndex, before.ClippedStart, before.ClippedEnd));
    }

    // -- Missed ----------------------------------------------------------------------

    [Fact]
    public void A_deadline_is_missed_once_its_day_has_ended_and_the_task_is_not_finished()
    {
        Assert.True(Timeline.IsMissed(Task(deadline: Today.AddDays(-1)), Today));
        Assert.False(Timeline.IsMissed(Task(deadline: Today), Today));
        Assert.False(Timeline.IsMissed(Task(deadline: Today.AddDays(-1), status: TaskState.Finished), Today));
        Assert.False(Timeline.IsMissed(Task(), Today));
        Assert.Equal(3, Timeline.DaysOverdue(Task(deadline: Today.AddDays(-3)), Today));
    }

    [Fact]
    public void A_task_is_done_once_it_is_finished_and_its_last_day_has_ended()
    {
        var past = new DaySpan(Today.AddDays(-5), Today.AddDays(-1));

        Assert.True(Timeline.IsDone(Task(status: TaskState.Finished), past, Today));
        Assert.False(Timeline.IsDone(Task(status: TaskState.Finished), past with { End = Today }, Today));
        Assert.False(Timeline.IsDone(Task(status: TaskState.InProgress), past, Today));
    }

    // -- Resizing ----------------------------------------------------------------------

    [Fact]
    public void Dragging_the_right_edge_moves_the_deadline_and_keeps_the_start()
    {
        var task = Task(start: Today, deadline: Today.AddDays(4));

        Assert.Equal((Today, Today.AddDays(7)), Timeline.Resize(task, Today, Edge.End, Today.AddDays(7)));
        // The start can't pass the deadline, so the bar never gets shorter than a day.
        Assert.Equal((Today, Today), Timeline.Resize(task, Today, Edge.End, Today.AddDays(-3)));
    }

    [Fact]
    public void Dragging_the_left_edge_gives_even_a_created_date_start_an_explicit_start()
    {
        var task = Task(deadline: Today.AddDays(4));

        Assert.Equal((Today.AddDays(1), Today.AddDays(4)), Timeline.Resize(task, Today, Edge.Start, Today.AddDays(1)));
        Assert.Equal((Today.AddDays(4), Today.AddDays(4)), Timeline.Resize(task, Today, Edge.Start, Today.AddDays(9)));
    }

    [Fact]
    public void Dragging_the_right_edge_of_a_pill_gives_it_a_deadline_with_the_start_on_its_day()
    {
        var anchor = Today.AddDays(2);

        Assert.Equal((anchor, Today.AddDays(8)), Timeline.Resize(Task(), anchor, Edge.End, Today.AddDays(8)));
        Assert.Equal((Today.AddDays(-1), anchor), Timeline.Resize(Task(), anchor, Edge.Start, Today.AddDays(-1)));
    }

    // -- Adding and moving ----------------------------------------------------------------

    [Fact]
    public void A_dateless_task_added_today_or_later_gets_that_one_day()
    {
        Assert.Equal(new DaySpan(Today, Today), Timeline.ScheduleOnAdd(Today, Today));
        Assert.Equal(new DaySpan(Today.AddDays(4), Today.AddDays(4)), Timeline.ScheduleOnAdd(Today.AddDays(4), Today));
    }

    [Fact]
    public void A_dateless_task_added_on_a_past_day_runs_through_today()
    {
        Assert.Equal(new DaySpan(Today.AddDays(-5), Today), Timeline.ScheduleOnAdd(Today.AddDays(-5), Today));
    }

    [Fact]
    public void Moving_a_row_shifts_both_ends_and_keeps_its_length()
    {
        var span = new DaySpan(Today, Today.AddDays(4));

        Assert.Equal(new DaySpan(Today.AddDays(3), Today.AddDays(7)), Timeline.Move(span, 3));
        Assert.Equal(new DaySpan(Today.AddDays(-2), Today.AddDays(2)), Timeline.Move(span, -2));
    }

    [Fact]
    public void A_move_stops_at_the_window_edge_and_never_goes_backwards()
    {
        var window = new TimelineWindow(Today.AddDays(-10), Today.AddDays(10));
        var span = new DaySpan(Today, Today.AddDays(4));

        Assert.Equal(6, window.ClampMove(span, 9));     // the end reaches the last day
        Assert.Equal(-10, window.ClampMove(span, -30)); // the start reaches the first day
        Assert.Equal(2, window.ClampMove(span, 2));

        // Already past an edge (a clipped bar): it can't be pushed further out,
        // and the clamp doesn't turn a push into a pull.
        var clipped = new DaySpan(Today.AddDays(-20), Today);
        Assert.Equal(0, window.ClampMove(clipped, -3));
        Assert.Equal(5, window.ClampMove(clipped, 5));
    }

    // -- Height -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 9)]
    [InlineData(7, 9)]
    [InlineData(8, 10)]  // past the minimum, two empty lines stay below the last bar
    [InlineData(30, 32)]
    public void The_grid_keeps_nine_lines_at_least_and_two_spare(int lines, int rows) =>
        Assert.Equal(rows, Timeline.Rows(lines));

    // -- Rows and lines -------------------------------------------------------------------

    private static readonly TimelineWindow Window = new(Today.AddDays(-30), Today.AddDays(60));

    /// <summary>An entry on the row of <paramref name="rank"/> whose bar runs over the days given, counted from today.</summary>
    private static RoadmapEntryDto Entry(string name, decimal rank, int from, int to) =>
        new(Guid.NewGuid(), rank, Today, "FEAT", "Features",
            Task(start: Today.AddDays(from), deadline: Today.AddDays(to)) with { Name = name });

    private static List<List<string>> Lines(TimelineLayout layout) =>
        layout.Lines.Select(l => l.Entries.Select(e => e.Task.Name).ToList()).ToList();

    [Fact]
    public void Entries_sharing_a_rank_share_a_line_left_to_right_and_rows_go_in_rank_order()
    {
        var layout = Timeline.Layout(
        [
            Entry("Build", 2048m, 11, 20),
            Entry("Polish", 3072m, 0, 5),
            Entry("Test", 2048m, 21, 25),
            Entry("Design", 2048m, 0, 10),
            Entry("Alpha", 1024m, 0, 3),
        ], Window);

        Assert.Equal([["Alpha"], ["Design", "Build", "Test"], ["Polish"]], Lines(layout));
        Assert.Equal([0, 1, 2], layout.Lines.Select(l => l.Row));
        Assert.Equal(["Alpha", "Design", "Build", "Test", "Polish"], layout.ReadingOrder.Select(e => e.Task.Name));
    }

    /// <summary>A row whose bars overlap takes as few extra lines as hold them, straight below it.</summary>
    [Fact]
    public void A_row_whose_bars_overlap_takes_lines_below_it_and_no_more_than_it_needs()
    {
        var design = Entry("Design", 1024m, 0, 15); // stretched into Build
        var build = Entry("Build", 1024m, 11, 20);
        var test = Entry("Test", 1024m, 21, 25);
        var after = Entry("After", 2048m, 0, 1);

        var layout = Timeline.Layout([design, build, test, after], Window);

        Assert.Equal([["Design", "Test"], ["Build"], ["After"]], Lines(layout));
        Assert.Equal([0, 0, 1], layout.Lines.Select(l => l.Row));
        Assert.Equal((1, 0), (layout.LineOf(build.Id), layout.RowOf(build.Id)));
        Assert.Equal((2, 1), (layout.LineOf(after.Id), layout.RowOf(after.Id)));
        Assert.Equal(-1, layout.LineOf(Guid.NewGuid()));
        Assert.Equal([3, 1], layout.Rows.Select(r => r.Count));
    }

    /// <summary>Bars that only touch, one ending the day before the next starts, fit on one line.</summary>
    [Fact]
    public void Bars_end_to_end_fit_and_bars_sharing_a_day_do_not()
    {
        BarPlacement Bar(int from, int to) => new(from, to, false, false);

        Assert.True(Timeline.Fits([Bar(11, 20)], [Bar(0, 10), Bar(21, 25)]));
        Assert.False(Timeline.Fits([Bar(10, 20)], [Bar(0, 10)]));
        // Bars moving together have to fit beside one another, too.
        Assert.False(Timeline.Fits([Bar(30, 32), Bar(32, 34)], [Bar(0, 10)]));
        Assert.True(Timeline.Fits([Bar(30, 31), Bar(32, 34)], []));
    }

    /// <summary>Two bars long gone past the window's edge are both stubs on its first day, and so overlap.</summary>
    [Fact]
    public void Overlap_is_judged_on_bars_as_drawn_clipped_to_the_window()
    {
        var layout = Timeline.Layout(
            [Entry("Old", 1024m, -90, -80), Entry("Older", 1024m, -70, -60)], Window);

        Assert.Equal(2, layout.Lines.Count);
    }

    // -- Virtualization -----------------------------------------------------------------

    [Fact]
    public void Only_columns_near_the_view_are_rendered_and_recut_only_near_their_edge()
    {
        var visible = Viewport.Visible(scrollLeft: 4000, width: 1200, dayWidth: 40, days: 1096);
        Assert.Equal((100, 130), visible);

        var rendered = Viewport.Rendered(visible, 1096);
        Assert.Equal((100 - Viewport.Buffer, 130 + Viewport.Buffer), rendered);

        Assert.False(Viewport.NeedsRecut((110, 140), rendered, 1096));
        Assert.True(Viewport.NeedsRecut((150, 180), rendered, 1096));
        // At the window's own edge there's nothing more to render.
        Assert.False(Viewport.NeedsRecut((0, 30), (0, 75), 1096));
    }

    [Fact]
    public void The_window_spans_the_configured_days_either_side_of_today()
    {
        var window = TimelineWindow.Around(Today, new RoadmapSettingsDto(365, 730, DayOfWeek.Monday, 2, 1));

        Assert.Equal(1096, window.Days);
        Assert.Equal(365, window.IndexOf(Today));
    }

    // -- Milestones ------------------------------------------------------------

    private static readonly RoadmapSettingsDto WeeklyFromMonday = new(365, 730, DayOfWeek.Monday, 1, 1);

    [Theory]
    [InlineData("2026-10-01", "2026-09-28", true)]  // the sprint's first day, for a milestone on its fourth
    [InlineData("2026-10-01", "2026-10-01", true)]  // its own day
    [InlineData("2026-10-01", "2026-09-27", false)] // the sprint before, for a milestone that isn't early
    [InlineData("2026-10-01", "2026-10-02", false)] // its day has ended
    [InlineData("2026-09-29", "2026-09-21", true)]  // two days in: the sprint before counts
    [InlineData("2026-09-29", "2026-09-20", false)] // but not the one before that
    public void A_milestone_is_due_from_its_sprints_start_through_its_day(string day, string today, bool due) =>
        Assert.Equal(due, Timeline.IsMilestoneDue(DateOnly.Parse(day), DateOnly.Parse(today), WeeklyFromMonday));

    [Theory]
    [InlineData("#e53935", "#fff")]
    [InlineData("#1e88e5", "#fff")]
    [InlineData("#fdd835", "#000")]
    [InlineData("#ffffff", "#000")]
    [InlineData("#000000", "#fff")]
    [InlineData("not a colour", "#fff")]
    public void A_flag_gets_the_ink_that_reads_on_it(string color, string ink) =>
        Assert.Equal(ink, MilestoneColors.Ink(color));
}
