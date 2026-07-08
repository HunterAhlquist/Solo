using Solo.Data.Domain;
using Solo.Shared;
using Xunit;

namespace Solo.Data.Tests;

public class TaskStateCalculatorTests
{
    [Theory]
    [InlineData(TaskState.Unstarted)]
    [InlineData(TaskState.InProgress)]
    [InlineData(TaskState.Finished)]
    public void WithoutSubTasks_KeepsManualStatus(TaskState manual) =>
        Assert.Equal(manual, TaskStateCalculator.Derive(0, 0, manual));

    [Theory]
    [InlineData(3, 0, TaskState.Unstarted)]
    [InlineData(3, 1, TaskState.InProgress)]
    [InlineData(3, 2, TaskState.InProgress)]
    [InlineData(3, 3, TaskState.Finished)]
    [InlineData(1, 0, TaskState.Unstarted)]
    [InlineData(1, 1, TaskState.Finished)]
    public void WithSubTasks_DerivesFromFinishedCount(int total, int finished, TaskState expected) =>
        Assert.Equal(expected, TaskStateCalculator.Derive(total, finished, TaskState.Finished));

    [Fact]
    public void WithSubTasks_ManualStatusIsIgnored()
    {
        // Even if someone manually set Finished before sub-tasks existed.
        Assert.Equal(TaskState.Unstarted, TaskStateCalculator.Derive(2, 0, TaskState.Finished));
        Assert.Equal(TaskState.InProgress, TaskStateCalculator.Derive(2, 1, TaskState.Unstarted));
    }
}
