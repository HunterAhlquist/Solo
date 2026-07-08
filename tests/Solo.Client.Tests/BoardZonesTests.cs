using Solo.Client.Components;
using Solo.Shared;

namespace Solo.Client.Tests;

public class BoardZonesTests
{
    [Fact]
    public void A_zone_id_round_trips_through_an_epic_and_a_status()
    {
        var epicId = Guid.NewGuid();

        var parsed = BoardZones.TryParse(BoardZones.Id(epicId, TaskState.InProgress), out var actualEpic, out var status);

        Assert.True(parsed);
        Assert.Equal(epicId, actualEpic);
        Assert.Equal(TaskState.InProgress, status);
    }

    [Fact]
    public void The_adhoc_band_round_trips_as_a_null_epic()
    {
        var parsed = BoardZones.TryParse(BoardZones.Id(null, TaskState.Finished), out var epicId, out var status);

        Assert.True(parsed);
        Assert.Null(epicId);
        Assert.Equal(TaskState.Finished, status);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("not-a-guid:1")]
    [InlineData("adhoc:99")]
    [InlineData("adhoc:notanumber")]
    public void A_malformed_zone_id_is_rejected_rather_than_guessed_at(string zoneId)
    {
        Assert.False(BoardZones.TryParse(zoneId, out _, out _));
    }

    [Fact]
    public void Every_status_gets_a_column_title()
    {
        Assert.Equal("Unstarted", BoardZones.Title(TaskState.Unstarted));
        Assert.Equal("In Progress", BoardZones.Title(TaskState.InProgress));
        Assert.Equal("Finished", BoardZones.Title(TaskState.Finished));
    }
}

/// <summary>The roll-up rule the client applies optimistically is the server's own.</summary>
public class TaskStateCalculatorTests
{
    [Fact]
    public void A_task_without_sub_tasks_keeps_its_manual_status()
    {
        Assert.Equal(TaskState.InProgress, TaskStateCalculator.Derive(0, 0, TaskState.InProgress));
    }

    [Theory]
    [InlineData(3, 0, TaskState.Unstarted)]
    [InlineData(3, 1, TaskState.InProgress)]
    [InlineData(3, 3, TaskState.Finished)]
    public void Sub_tasks_derive_the_status(int total, int finished, TaskState expected)
    {
        Assert.Equal(expected, TaskStateCalculator.Derive(total, finished, TaskState.Unstarted));
    }
}
