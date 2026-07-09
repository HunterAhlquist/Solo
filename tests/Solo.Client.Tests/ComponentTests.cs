using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>
/// Renders MudBlazor components in isolation. Two accommodations are needed:
/// popovers normally assert that a <c>MudPopoverProvider</c> is on the page (there
/// is no layout here), and several MudBlazor services are async-disposable, which
/// the synchronous xunit teardown can't handle — hence <see cref="IAsyncLifetime"/>.
/// </summary>
public abstract class ComponentTestBase : BunitContext, IAsyncLifetime
{
    protected ComponentTestBase()
    {
        // MudBlazor components chatter to JS on render; we're asserting markup.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}

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
}

/// <summary>Save-event semantics: blur/explicit save commits, keystrokes never do.</summary>
public class MarkdownEditorTests : ComponentTestBase
{
    [Fact]
    public void The_textarea_binds_no_per_keystroke_handler()
    {
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.AlwaysEditing, true));

        // Immediate="false": there is literally no `oninput` to fire, which is
        // what "edits never commit per keystroke" means at the DOM level.
        Assert.Throws<Bunit.MissingEventHandlerException>(
            () => editor.Find("textarea").Input("half-typed thought"));
    }

    [Fact]
    public void Updating_the_field_does_not_commit_until_an_explicit_save()
    {
        var saves = new List<string>();
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.AlwaysEditing, true)
            .Add(c => c.OnSave, body => saves.Add(body)));

        editor.Find("textarea").Change("half-typed thought");

        Assert.Empty(saves);
    }

    [Fact]
    public void The_save_button_commits_the_draft()
    {
        var saves = new List<string>();
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.AlwaysEditing, true)
            .Add(c => c.SaveText, "Save")
            .Add(c => c.OnSave, body => saves.Add(body)));

        editor.Find("textarea").Change("## Design notes");
        editor.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        Assert.Equal("## Design notes", Assert.Single(saves));
    }

    [Fact]
    public void A_read_only_viewer_never_gets_an_editor()
    {
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, false)
            .Add(c => c.Value, "Some description"));

        editor.Find(".markdown-read").Click();

        Assert.Empty(editor.FindAll("textarea"));
    }

    [Fact]
    public void Opening_and_closing_the_editor_reports_editing_presence()
    {
        var reports = new List<bool>();
        var editor = Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.Value, "text")
            .Add(c => c.OnEditingChanged, editing => reports.Add(editing)));

        editor.Find(".markdown-read").Click();
        Assert.Equal([true], reports);

        editor.FindAll("button").First(b => b.TextContent.Contains("Cancel")).Click();
        Assert.Equal([true, false], reports);
    }
}

public class EditingWarningTests : ComponentTestBase
{
    [Fact]
    public void Nobody_editing_means_no_warning()
    {
        var warning = Render<EditingWarning>(p => p.Add(c => c.Editors, []));

        Assert.Empty(warning.Markup.Trim());
    }

    [Fact]
    public void One_editor_is_named()
    {
        var warning = Render<EditingWarning>(p => p.Add(c => c.Editors,
        [
            new PresenceEntry(TestData.OtherId, "Hunter", EntityTypes.Task, Guid.NewGuid(), PresenceMode.Editing),
        ]));

        Assert.Contains("Hunter is editing this right now", warning.Markup);
    }

    [Fact]
    public void Several_editors_collapse_to_a_count()
    {
        var taskId = Guid.NewGuid();
        var warning = Render<EditingWarning>(p => p.Add(c => c.Editors,
        [
            new PresenceEntry(Guid.NewGuid(), "Hunter", EntityTypes.Task, taskId, PresenceMode.Editing),
            new PresenceEntry(Guid.NewGuid(), "Ada", EntityTypes.Task, taskId, PresenceMode.Editing),
            new PresenceEntry(Guid.NewGuid(), "Grace", EntityTypes.Task, taskId, PresenceMode.Editing),
        ]));

        Assert.Contains("Hunter and 2 others", warning.Markup);
    }
}

/// <summary>The reconnecting hint only speaks up on a board whose feed is down.</summary>
public class ConnectionIndicatorTests : ComponentTestBase
{
    private IRenderedComponent<ConnectionIndicator> RenderIndicator(bool active, bool connected)
    {
        Services.AddSingleton<IBoardHubClient>(new FakeBoardHub { IsActive = active, IsConnected = connected });
        return Render<ConnectionIndicator>();
    }

    [Fact]
    public void Off_a_board_it_stays_silent_even_when_not_connected()
    {
        // The bug: on the project list (no board joined) the "disconnected" icon showed.
        var cut = RenderIndicator(active: false, connected: false);

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void On_a_board_with_a_live_connection_it_stays_silent()
    {
        var cut = RenderIndicator(active: true, connected: true);

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void On_a_board_whose_connection_dropped_it_warns()
    {
        var cut = RenderIndicator(active: true, connected: false);

        Assert.NotEmpty(cut.Markup.Trim());
    }
}

public class LabelChipTests : ComponentTestBase
{
    [Theory]
    [InlineData("#ecc94b", "#15151f")] // light label -> dark text
    [InlineData("#5b4bd6", "#ffffff")] // dark label  -> light text
    public void Text_colour_follows_the_labels_luminance(string background, string expectedText)
    {
        var chip = Render<LabelChip>(p => p.Add(c => c.Label, TestData.Label(Guid.NewGuid(), "L", background)));

        Assert.Contains($"color:{expectedText}", chip.Markup);
    }

    [Fact]
    public void No_label_renders_nothing()
    {
        var chip = Render<LabelChip>(p => p.Add(c => c.Label, null));

        Assert.Empty(chip.Markup.Trim());
    }
}
