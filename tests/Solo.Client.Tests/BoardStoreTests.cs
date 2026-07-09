using System.Net;
using Solo.Client.Api;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>
/// The change feed carries field names, not values (frontend plan §4), so every
/// event turns into a refetch. These pin down <em>which</em> refetch.
/// </summary>
public class BoardStoreTests
{
    private static async Task<(BoardStore Store, StubHttp Http, FakeBoardHub Hub)> LoadedStoreAsync(
        BoardDetailDto? board = null)
    {
        var http = new StubHttp();
        http.RespondJson(board ?? TestData.Board());              // GET /api/boards/{id}
        http.RespondJson(Array.Empty<ProjectMemberDto>());        // GET /api/projects/{id}/members

        var hub = new FakeBoardHub();
        var api = http.CreateClient();
        var store = new BoardStore(api, hub, new UserSession(api));

        await store.LoadAsync(TestData.BoardId);
        return (store, http, hub);
    }

    [Fact]
    public async Task Loading_a_board_joins_its_hub_group()
    {
        var (_, _, hub) = await LoadedStoreAsync();

        Assert.Equal([TestData.BoardId], hub.Joined);
    }

    [Fact]
    public async Task A_task_change_refetches_only_that_task()
    {
        var task = TestData.Task(status: TaskState.Unstarted);
        var (store, http, hub) = await LoadedStoreAsync(TestData.Board(tasks: [task]));
        http.RespondJson(task with { Status = TaskState.Finished });

        await hub.RaiseEntityChanged(
            new EntityChangedEvent(EntityTypes.Task, task.Id, ChangeKind.Updated, ["Status"], TestData.OtherId));

        Assert.Equal($"GET /api/tasks/{task.Id}", http.Paths[^1]);
        Assert.Equal(TaskState.Finished, store.Board!.Tasks.Single().Status);
    }

    [Fact]
    public async Task A_task_deletion_drops_it_without_a_refetch()
    {
        var task = TestData.Task();
        var (store, http, hub) = await LoadedStoreAsync(TestData.Board(tasks: [task]));
        var callsBefore = http.Requests.Count;

        await hub.RaiseEntityChanged(
            new EntityChangedEvent(EntityTypes.Task, task.Id, ChangeKind.Deleted, [], TestData.OtherId));

        Assert.Empty(store.Board!.Tasks);
        Assert.Equal(callsBefore, http.Requests.Count);
    }

    [Fact]
    public async Task A_sub_task_change_refetches_its_parent_task_whose_status_may_have_rolled_up()
    {
        var subTask = TestData.SubTask(false);
        var task = TestData.Task(subTasks: [subTask]);
        var (_, http, hub) = await LoadedStoreAsync(TestData.Board(tasks: [task]));
        http.RespondJson(task);

        await hub.RaiseEntityChanged(
            new EntityChangedEvent(EntityTypes.SubTask, subTask.Id, ChangeKind.Updated, ["IsFinished"], TestData.OtherId));

        Assert.Equal($"GET /api/tasks/{task.Id}", http.Paths[^1]);
    }

    [Fact]
    public async Task A_comment_change_notifies_the_drawer_and_does_not_refetch_the_board()
    {
        // Comments aren't part of BoardDetailDto, so refetching the board would
        // tell us nothing. Only an open drawer cares.
        var (store, http, hub) = await LoadedStoreAsync();
        var callsBefore = http.Requests.Count;
        var notified = 0;
        store.CommentsChanged += () => notified++;

        await hub.RaiseEntityChanged(
            new EntityChangedEvent(EntityTypes.Comment, Guid.NewGuid(), ChangeKind.Created, [], TestData.OtherId));

        Assert.Equal(1, notified);
        Assert.Equal(callsBefore, http.Requests.Count);
    }

    [Fact]
    public async Task A_structural_change_debounces_into_one_full_board_refetch()
    {
        var (store, http, hub) = await LoadedStoreAsync();
        http.RespondJson(TestData.Board());
        var callsBefore = http.Requests.Count;

        // Three events in a burst: the first two are superseded.
        await hub.RaiseEntityChanged(new EntityChangedEvent(EntityTypes.Epic, TestData.EpicId, ChangeKind.Created, [], TestData.OtherId));
        await hub.RaiseEntityChanged(new EntityChangedEvent(EntityTypes.Label, Guid.NewGuid(), ChangeKind.Updated, ["Color"], TestData.OtherId));
        await hub.RaiseEntityChanged(new EntityChangedEvent(EntityTypes.Board, TestData.BoardId, ChangeKind.Updated, ["Name"], TestData.OtherId));

        // The store's own task, not a wall-clock poll: a starved CI runner
        // can stretch the 150ms debounce past any deadline a poll would pick.
        await store.PendingRefetch;

        Assert.Equal(callsBefore + 1, http.Requests.Count);
        Assert.Equal($"GET /api/boards/{TestData.BoardId}", http.Paths[^1]);
    }

    [Fact]
    public async Task Presence_excludes_yourself_and_surfaces_other_editors()
    {
        var http = new StubHttp();
        http.RespondJson(TestData.Board());
        http.RespondJson(Array.Empty<ProjectMemberDto>());
        http.RespondJson(new UserDto(TestData.MeId, "me@example.com", "Me", null, false));

        var hub = new FakeBoardHub();
        var api = http.CreateClient();
        var session = new UserSession(api);
        var store = new BoardStore(api, hub, session);
        await store.LoadAsync(TestData.BoardId);
        await session.LoadAsync();

        var taskId = Guid.NewGuid();
        await hub.RaisePresence(new BoardPresenceSnapshot(TestData.BoardId,
        [
            new PresenceEntry(TestData.MeId, "Me", EntityTypes.Task, taskId, PresenceMode.Editing),
            new PresenceEntry(TestData.OtherId, "Hunter", EntityTypes.Task, taskId, PresenceMode.Editing),
        ]));

        var editors = store.EditorsOn(EntityTypes.Task, taskId);

        Assert.Equal(TestData.OtherId, Assert.Single(editors).UserId);
    }

    [Fact]
    public async Task A_presence_snapshot_for_another_board_is_ignored()
    {
        var (store, _, hub) = await LoadedStoreAsync();

        await hub.RaisePresence(new BoardPresenceSnapshot(Guid.NewGuid(),
            [new PresenceEntry(TestData.OtherId, "Hunter", EntityTypes.Board, Guid.NewGuid(), PresenceMode.Viewing)]));

        Assert.Empty(store.Presence);
    }

    [Fact]
    public async Task Detaching_leaves_the_board_and_clears_state()
    {
        var (store, _, hub) = await LoadedStoreAsync();

        await store.DetachAsync();

        Assert.Equal([TestData.BoardId], hub.Left);
        Assert.Null(store.Board);
    }

    [Fact]
    public async Task A_refetch_of_a_task_that_vanished_falls_back_to_the_board()
    {
        var task = TestData.Task();
        var (_, http, hub) = await LoadedStoreAsync(TestData.Board(tasks: [task]));
        http.Respond(HttpStatusCode.NotFound, """{"title":"Not Found"}""");  // GET /api/tasks/{id}
        http.RespondJson(TestData.Board());                                  // fallback GET /api/boards/{id}

        await hub.RaiseEntityChanged(
            new EntityChangedEvent(EntityTypes.Task, task.Id, ChangeKind.Updated, ["Name"], TestData.OtherId));

        Assert.Equal($"GET /api/boards/{TestData.BoardId}", http.Paths[^1]);
    }
}
