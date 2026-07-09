using Solo.Client.State;
using Solo.Shared;

namespace Solo.Client.Tests;

/// <summary>Which endpoint a drop hits, per drop zone (frontend plan §6).</summary>
public class DropPlanTests
{
    [Fact]
    public void Drop_within_the_same_zone_only_reranks()
    {
        var task = TestData.Task(epicId: TestData.EpicId, status: TaskState.InProgress);

        var plan = DropPlan.For(task, TestData.EpicId, TaskState.InProgress);

        Assert.False(plan.ChangeEpic);
        Assert.False(plan.ChangeStatus);
    }

    [Fact]
    public void Drop_across_columns_changes_status()
    {
        var task = TestData.Task(epicId: TestData.EpicId, status: TaskState.Unstarted);

        var plan = DropPlan.For(task, TestData.EpicId, TaskState.Finished);

        Assert.True(plan.ChangeStatus);
        Assert.False(plan.ChangeEpic);
    }

    [Fact]
    public void Drop_across_swimlanes_changes_epic()
    {
        var task = TestData.Task(epicId: TestData.EpicId, status: TaskState.Unstarted);
        var otherEpic = Guid.NewGuid();

        var plan = DropPlan.For(task, otherEpic, TaskState.Unstarted);

        Assert.True(plan.ChangeEpic);
        Assert.False(plan.ChangeStatus);
    }

    [Fact]
    public void Drop_into_the_adhoc_band_detaches_the_epic()
    {
        var task = TestData.Task(epicId: TestData.EpicId);

        var plan = DropPlan.For(task, null, TaskState.Unstarted);

        Assert.True(plan.ChangeEpic);
    }

    [Fact]
    public void Task_with_subtasks_may_still_change_columns()
    {
        // Sub-tasks auto-complete a card but no longer lock its column: a manual
        // move to any status is a plain status change now, never a rejection.
        var task = TestData.Task(status: TaskState.Unstarted, subTasks: [TestData.SubTask(false)]);

        var plan = DropPlan.For(task, null, TaskState.Finished);

        Assert.True(plan.ChangeStatus);
    }

    [Fact]
    public void Task_with_subtasks_still_moves_between_swimlanes()
    {
        var task = TestData.Task(
            epicId: TestData.EpicId,
            status: TaskState.InProgress,
            subTasks: [TestData.SubTask(true), TestData.SubTask(false)]);

        var plan = DropPlan.For(task, null, TaskState.InProgress);

        Assert.False(plan.ChangeStatus);
        Assert.True(plan.ChangeEpic);
    }
}

public class RankNeighborsTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000000");

    [Fact]
    public void Empty_zone_yields_no_neighbours_so_the_caller_skips_the_rerank()
    {
        var (preceding, following) = RankNeighbors.Compute([], 0);

        Assert.Null(preceding);
        Assert.Null(following);
    }

    [Fact]
    public void Head_of_zone_has_only_a_following_sibling()
    {
        var (preceding, following) = RankNeighbors.Compute([A, B], 0);

        Assert.Null(preceding);
        Assert.Equal(A, following);
    }

    [Fact]
    public void Middle_of_zone_has_both_siblings()
    {
        var (preceding, following) = RankNeighbors.Compute([A, B, C], 1);

        Assert.Equal(A, preceding);
        Assert.Equal(B, following);
    }

    [Fact]
    public void Tail_of_zone_has_only_a_preceding_sibling()
    {
        var (preceding, following) = RankNeighbors.Compute([A, B], 2);

        Assert.Equal(B, preceding);
        Assert.Null(following);
    }

    [Fact]
    public void Index_beyond_the_zone_clamps_to_the_tail()
    {
        var (preceding, following) = RankNeighbors.Compute([A, B], 99);

        Assert.Equal(B, preceding);
        Assert.Null(following);
    }
}
