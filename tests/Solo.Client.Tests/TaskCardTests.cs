using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Client.Theming;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>Status-rollup display and presence badges (frontend plan §9).</summary>
public class TaskCardTests : ComponentTestBase
{
    [Fact]
    public void A_task_with_sub_tasks_shows_its_rollup_progress()
    {
        var task = TestData.Task(subTasks:
            [TestData.SubTask(true), TestData.SubTask(true), TestData.SubTask(false)]);

        var card = Render<TaskCard>(p => p.Add(c => c.Task, task));

        Assert.Contains("2/3", card.Markup);
    }

    [Fact]
    public void A_task_without_sub_tasks_shows_no_progress_ring()
    {
        var card = Render<TaskCard>(p => p.Add(c => c.Task, TestData.Task()));

        Assert.Empty(card.FindAll(".mud-progress-circular"));
    }

    [Fact]
    public void The_label_renders_with_its_own_colour()
    {
        var labelId = Guid.NewGuid();
        var card = Render<TaskCard>(p => p
            .Add(c => c.Task, TestData.Task(labelId: labelId))
            .Add(c => c.Label, TestData.Label(labelId, "Blocker", "#f56565")));

        Assert.Contains("Blocker", card.Markup);
        Assert.Contains("#f56565", card.Markup);
    }

    [Fact]
    public void Someone_editing_this_card_gets_a_pulsing_avatar()
    {
        var task = TestData.Task();
        var card = Render<TaskCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.Presence,
            [
                new PresenceEntry(TestData.OtherId, "Hunter", EntityTypes.Task, task.Id, PresenceMode.Editing),
            ]));

        Assert.NotEmpty(card.FindAll(".presence-avatar--editing"));
    }

    [Fact]
    public void Someone_merely_viewing_this_card_gets_a_plain_avatar()
    {
        var task = TestData.Task();
        var card = Render<TaskCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.Presence,
            [
                new PresenceEntry(TestData.OtherId, "Hunter", EntityTypes.Task, task.Id, PresenceMode.Viewing),
            ]));

        Assert.Empty(card.FindAll(".presence-avatar--editing"));
    }

    [Fact]
    public void A_remote_change_flashes_the_card_outline_once()
    {
        var card = Render<TaskCard>(p => p
            .Add(c => c.Task, TestData.Task())
            .Add(c => c.Pulse, true));

        Assert.NotEmpty(card.FindAll(".solo-pulse"));
    }

    [Fact]
    public void A_task_without_a_deadline_leaves_the_corner_blank()
    {
        var card = Render<TaskCard>(p => p.Add(c => c.Task, TestData.Task()));

        Assert.Empty(card.FindAll(".deadline-ring"));
        Assert.Empty(card.FindAll(".kanban-card--deadline"));
    }

    [Fact]
    public void A_near_deadline_shows_the_days_left_and_tints_the_outline()
    {
        var task = TestData.Task(
            createdAt: DateTime.UtcNow.AddDays(-10),
            deadline: DateOnly.FromDateTime(DateTime.Now).AddDays(5));

        var card = Render<TaskCard>(p => p.Add(c => c.Task, task));

        Assert.Equal("5", card.Find(".deadline-ring__days").TextContent);
        Assert.NotEmpty(card.FindAll(".kanban-card--deadline"));
        Assert.Contains("--deadline-color:hsl(", card.Markup);
        Assert.Empty(card.FindAll(".deadline-ring--overdue"));
    }

    /// <summary>
    /// The countdown ring is the sub-task rollup's own component, recoloured —
    /// which is what keeps the two rings on a card the same size and weight.
    /// </summary>
    [Fact]
    public void The_deadline_ring_is_the_same_progress_circle_as_the_subtask_rollup()
    {
        var task = TestData.Task(
            createdAt: DateTime.UtcNow.AddDays(-10),
            deadline: DateOnly.FromDateTime(DateTime.Now).AddDays(5),
            subTasks: [TestData.SubTask(true), TestData.SubTask(false)]);

        var card = Render<TaskCard>(p => p.Add(c => c.Task, task));

        var rings = card.FindAll(".mud-progress-circular");
        Assert.Equal(2, rings.Count);
        Assert.All(rings, r => Assert.Contains("mud-progress-small", r.ClassName!));
        Assert.Contains("color:var(--deadline-color)",
            card.Find(".deadline-ring .mud-progress-circular").GetAttribute("style"));
    }

    [Fact]
    public void A_far_deadline_shows_the_ring_but_hides_the_three_digit_count()
    {
        var task = TestData.Task(
            deadline: DateOnly.FromDateTime(DateTime.Now).AddDays(150));

        var card = Render<TaskCard>(p => p.Add(c => c.Task, task));

        Assert.NotEmpty(card.FindAll(".deadline-ring"));
        Assert.Empty(card.FindAll(".deadline-ring__days"));
    }

    [Fact]
    public void A_missed_deadline_blinks_red()
    {
        var task = TestData.Task(
            createdAt: DateTime.UtcNow.AddDays(-10),
            deadline: DateOnly.FromDateTime(DateTime.Now).AddDays(-3));

        var card = Render<TaskCard>(p => p.Add(c => c.Task, task));

        Assert.NotEmpty(card.FindAll(".deadline-ring--overdue"));
        // Fully elapsed → hue 0, i.e. red — the outline shares the same variable.
        Assert.Contains("--deadline-color:hsl(0", card.Markup);
        Assert.Equal("0", card.Find(".deadline-ring__days").TextContent);
    }

    /// <summary>
    /// Once a task lands in Finished the countdown is moot — ring, tint and all —
    /// even when the deadline came and went while the task was still open.
    /// </summary>
    [Fact]
    public void A_finished_task_drops_every_trace_of_its_deadline()
    {
        var task = TestData.Task(
            status: TaskState.Finished,
            createdAt: DateTime.UtcNow.AddDays(-10),
            deadline: DateOnly.FromDateTime(DateTime.Now).AddDays(-3));

        var card = Render<TaskCard>(p => p.Add(c => c.Task, task));

        Assert.Empty(card.FindAll(".deadline-ring"));
        Assert.Empty(card.FindAll(".kanban-card--deadline"));
        Assert.DoesNotContain("--deadline-color", card.Markup);
    }

    [Fact]
    public void A_finished_task_fades_back_and_an_open_one_does_not()
    {
        var finished = Render<TaskCard>(p => p
            .Add(c => c.Task, TestData.Task(status: TaskState.Finished)));
        var open = Render<TaskCard>(p => p
            .Add(c => c.Task, TestData.Task(status: TaskState.InProgress)));

        Assert.NotEmpty(finished.FindAll(".kanban-card--finished"));
        Assert.Empty(open.FindAll(".kanban-card--finished"));
    }
}
