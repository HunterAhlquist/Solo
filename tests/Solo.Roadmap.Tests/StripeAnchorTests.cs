using Solo.Roadmap.Data;

namespace Solo.Roadmap.Tests;

/// <summary>The admin picks a day of year; what's stored is that day's date in the year it's saved.</summary>
public class StripeAnchorTests
{
    [Theory]
    [InlineData(1, 2026, "2026-01-01")]
    [InlineData(5, 2026, "2026-01-05")]
    [InlineData(60, 2026, "2026-03-01")]
    [InlineData(60, 2028, "2028-02-29")]
    [InlineData(365, 2026, "2026-12-31")]
    [InlineData(366, 2028, "2028-12-31")]
    public void A_day_of_year_resolves_to_its_date(int day, int year, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), RoadmapSettingsStore.ResolveAnchor(day, year));

    [Theory]
    [InlineData(366, 2026)]
    [InlineData(366, 2100)]
    [InlineData(0, 2028)]
    [InlineData(367, 2028)]
    public void A_day_the_year_does_not_have_resolves_to_nothing(int day, int year) =>
        Assert.Null(RoadmapSettingsStore.ResolveAnchor(day, year));
}
