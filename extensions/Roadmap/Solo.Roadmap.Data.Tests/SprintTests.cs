using Solo.Roadmap.Contracts;

namespace Solo.Roadmap.Data.Tests;

/// <summary>
/// Sprint stripes come from the settings alone: a start day and a length in
/// weeks, counted from a fixed reference rather than from when they were saved.
/// </summary>
public class SprintTests
{
    [Theory]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Sunday)]
    public void The_anchor_is_the_first_such_weekday_of_year_one(DayOfWeek startDay)
    {
        var anchor = RoadmapSprints.Anchor(startDay);

        Assert.Equal(startDay, anchor.DayOfWeek);
        Assert.InRange(anchor.DayNumber, 0, 6);
    }

    [Theory]
    [InlineData("2026-10-01", DayOfWeek.Monday, 1, "2026-09-28")]
    [InlineData("2026-09-28", DayOfWeek.Monday, 1, "2026-09-28")] // a start day starts its own sprint
    [InlineData("2026-10-04", DayOfWeek.Monday, 1, "2026-09-28")] // Sunday is the last day of it
    [InlineData("2026-10-01", DayOfWeek.Thursday, 1, "2026-10-01")]
    [InlineData("2026-09-30", DayOfWeek.Thursday, 1, "2026-09-24")]
    public void A_day_belongs_to_the_sprint_that_began_on_the_last_start_day(
        string day, DayOfWeek startDay, int weeks, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), RoadmapSprints.SprintStart(DateOnly.Parse(day), startDay, weeks));

    [Theory]
    [InlineData(DayOfWeek.Monday, 2)]
    [InlineData(DayOfWeek.Friday, 3)]
    [InlineData(DayOfWeek.Sunday, 4)]
    public void Every_sprint_starts_on_the_chosen_day_and_lasts_its_length(DayOfWeek startDay, int weeks)
    {
        // A run across a year boundary: the pattern doesn't restart at New Year.
        var starts = Enumerable.Range(0, 400)
            .Select(i => RoadmapSprints.SprintStart(new DateOnly(2026, 6, 1).AddDays(i), startDay, weeks))
            .Distinct()
            .ToList();

        Assert.All(starts, start => Assert.Equal(startDay, start.DayOfWeek));
        Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.Equal(weeks * 7, pair.Second.DayNumber - pair.First.DayNumber));
    }

    [Fact]
    public void The_pattern_does_not_depend_on_when_it_is_asked()
    {
        var early = RoadmapSprints.SprintStart(new DateOnly(1999, 3, 3), DayOfWeek.Tuesday, 2);
        var late = RoadmapSprints.SprintStart(new DateOnly(2031, 7, 9), DayOfWeek.Tuesday, 2);

        Assert.Equal(0, (late.DayNumber - early.DayNumber) % 14);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-09-28")] // a Thursday, three days in: due from its own sprint
    [InlineData("2026-10-04", "2026-09-28")] // the sprint's last day
    [InlineData("2026-09-30", "2026-09-21")] // a Wednesday, two days in: due from the sprint before
    [InlineData("2026-09-28", "2026-09-21")] // the first day of a sprint
    public void A_milestone_early_in_its_sprint_is_due_from_the_sprint_before(string day, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), RoadmapSprints.MilestoneDueFrom(DateOnly.Parse(day), DayOfWeek.Monday, 1));

    [Fact]
    public void The_sprint_before_is_a_whole_sprint_length_back()
    {
        var day = new DateOnly(2026, 9, 29);
        var start = RoadmapSprints.SprintStart(day, DayOfWeek.Monday, 3);

        Assert.Equal(start.AddDays(-21), RoadmapSprints.MilestoneDueFrom(start.AddDays(1), DayOfWeek.Monday, 3));
    }
}
