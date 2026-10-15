using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Components;

namespace Solo.Client.Tests;

public class DayRolloverTests : ComponentTestBase
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 15, 23, 50, 0, TimeSpan.Zero));

    public DayRolloverTests() => Services.AddSingleton<TimeProvider>(_clock);

    [Fact]
    public async Task Coming_back_on_a_later_day_rebuilds_the_page()
    {
        var rollover = Render<DayRollover>(p => p.AddChildContent<Probe>());
        var before = rollover.FindComponent<Probe>().Instance;

        _clock.Now = _clock.Now.AddMinutes(20);
        await rollover.Instance.OnPageReturned();

        Assert.NotSame(before, rollover.FindComponent<Probe>().Instance);
    }

    [Fact]
    public async Task Coming_back_the_same_day_leaves_the_page_alone()
    {
        var rollover = Render<DayRollover>(p => p.AddChildContent<Probe>());
        var before = rollover.FindComponent<Probe>().Instance;

        _clock.Now = _clock.Now.AddMinutes(5);
        await rollover.Instance.OnPageReturned();

        Assert.Same(before, rollover.FindComponent<Probe>().Instance);
    }

    /// <summary>Stands in for a page; the test only cares which instance is mounted.</summary>
    private sealed class Probe : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "page");
    }

    /// <summary>Local time is UTC, so the day turns over at the instant the test says.</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
