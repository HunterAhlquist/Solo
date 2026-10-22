using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Full-hierarchy CRUD, authz denials, audit rows, and concurrency conflicts
/// over a real Postgres (Testcontainers).
/// </summary>
[Collection("postgres")]
public sealed class BoardApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync("board_api");
        _factory = new SoloApiFactory(_connectionString);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(ProjectDto Project, BoardDto Board)> CreateProjectWithBoard(string name)
    {
        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name, alias = AliasOf(name) });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = $"{name} board", alias = AliasOf(name) });
        return (project, board);
    }

    /// <summary>"Round trip" → "ROUNDTRI": a valid alias derived from the test name.</summary>
    private static string AliasOf(string name)
    {
        var upper = new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return upper[..Math.Min(upper.Length, 8)];
    }

    [SkippableFact]
    public async Task EpicGroups_EnforceMembershipAndWriteAccess()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("GroupAuth");
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Village" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members", new { email = "groupreader@example.com", role = "Read" });
        var reader = _factory.As("groupreader", "groupreader@example.com");
        Assert.Single(await reader.GetJson<List<EpicGroupDto>>($"/api/boards/{board.Id}/groups"));
        Assert.Equal(group.Id, (await reader.GetJson<EpicGroupDto>($"/api/boards/{board.Id}/groups/{group.Id}")).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/boards/{board.Id}/groups", new { name = "Denied" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/boards/{board.Id}/groups/{group.Id}", new { name = "Denied", version = group.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/boards/{board.Id}/groups/{group.Id}?version={group.Version}")).StatusCode);
        var stranger = _factory.As("stranger", "stranger@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/boards/{board.Id}/groups")).StatusCode);
    }

    [SkippableFact]
    public async Task EpicGroups_ShareRootRanks_MoveEpics_AndDeleteWithoutLosingWork()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("Groups");
        var root = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Before" });
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Village 1" });
        var after = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "After" });
        Assert.True(root.Rank < group.Rank && group.Rank < after.Rank);
        var first = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Buildings", groupId = group.Id });
        var second = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "People", groupId = group.Id });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "House", epicId = first.Id });
        var adHoc = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Unrelated" });
        Assert.Null(root.GroupId);
        Assert.Equal(group.Id, first.GroupId);
        second = await _admin.PutJson<EpicDto>($"/api/epics/{second.Id}/group", new
        {
            groupId = group.Id, followingId = first.Id, version = second.Version,
        });
        Assert.True(second.Rank < first.Rank);
        var renamed = await _admin.PutJson<EpicGroupDto>($"/api/boards/{board.Id}/groups/{group.Id}", new { name = "Village", version = group.Version });
        var stale = await _admin.PutAsJsonAsync($"/api/boards/{board.Id}/groups/{group.Id}", new { name = "Stale", version = group.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var deleted = await _admin.DeleteAsync($"/api/boards/{board.Id}/groups/{group.Id}?version={renamed.Version}");
        deleted.EnsureSuccessStatusCode();
        var detail = await _admin.GetJson<BoardDetailDto>($"/api/boards/{board.Id}");
        Assert.Empty(detail.Groups);
        Assert.All(detail.Epics, e => Assert.Null(e.GroupId));
        Assert.Equal(new[] { root.Id, second.Id, first.Id, after.Id }, detail.Epics.Select(e => e.Id));
        Assert.Equal(first.Id, detail.Tasks.Single(t => t.Id == task.Id).EpicId);
        Assert.Null(detail.Tasks.Single(t => t.Id == adHoc.Id).EpicId);
    }

    [SkippableFact]
    public async Task EpicGroups_ValidateBoardAndNeighborScope_AndConcurrency()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("GroupScope");
        var (_, other) = await CreateProjectWithBoard("OtherScope");
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Group" });
        var foreign = await _admin.PostJson<EpicGroupDto>($"/api/boards/{other.Id}/groups", new { name = "Other" });
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Epic" });
        var invalidCreate = await _admin.PostAsJsonAsync($"/api/boards/{board.Id}/epics", new { name = "Invalid", groupId = foreign.Id });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
        var invalidMove = await _admin.PutAsJsonAsync($"/api/epics/{epic.Id}/group", new { groupId = foreign.Id, version = epic.Version });
        Assert.Equal(HttpStatusCode.BadRequest, invalidMove.StatusCode);
        var invalidNeighbor = await _admin.PutAsJsonAsync($"/api/epics/{epic.Id}/group", new { groupId = group.Id, followingId = foreign.Id, version = epic.Version });
        Assert.Equal(HttpStatusCode.BadRequest, invalidNeighbor.StatusCode);
        var moved = await _admin.PutJson<EpicDto>($"/api/epics/{epic.Id}/group", new { groupId = group.Id, version = epic.Version });
        Assert.Equal(group.Id, moved.GroupId);
        var stale = await _admin.PutAsJsonAsync($"/api/epics/{epic.Id}/group", new { groupId = (Guid?)null, version = epic.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var detached = await _admin.PutJson<EpicDto>($"/api/epics/{epic.Id}/group", new { groupId = (Guid?)null, precedingId = group.Id, version = moved.Version });
        Assert.Null(detached.GroupId);
        Assert.True(detached.Rank > group.Rank);
        var reranked = await _admin.PutJson<EpicGroupDto>($"/api/boards/{board.Id}/groups/{group.Id}/rank", new { precedingId = detached.Id, version = group.Version });
        Assert.True(reranked.Rank > detached.Rank);
        var wrongBoard = await _admin.DeleteAsync($"/api/boards/{other.Id}/groups/{group.Id}?version={reranked.Version}");
        Assert.Equal(HttpStatusCode.NotFound, wrongBoard.StatusCode);
        var staleDelete = await _admin.DeleteAsync($"/api/boards/{board.Id}/groups/{group.Id}?version={group.Version}");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
    }

    [SkippableFact]
    public async Task ProjectCreation_SeedsLabelDefaults_AndBoardsCopyThem()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Seeds");

        var defaults = await _admin.GetJson<List<LabelDto>>($"/api/projects/{project.Id}/labels");
        Assert.Equal(7, defaults.Count);
        Assert.Contains(defaults, l => l.Kind == LabelKind.Task && l.Name == "Bug");
        Assert.Contains(defaults, l => l.Kind == LabelKind.SubTask && l.Name == "Writing");
        Assert.All(defaults, l => Assert.Null(l.BoardId));

        // The board holds copies, not the defaults themselves.
        var boardLabels = await _admin.GetJson<List<LabelDto>>($"/api/boards/{board.Id}/labels");
        Assert.All(boardLabels, l => Assert.Equal(board.Id, l.BoardId));
        Assert.Equal(
            defaults.Select(l => (l.Kind, l.Name, l.Color)).OrderBy(x => x.Name),
            boardLabels.Select(l => (l.Kind, l.Name, l.Color)).OrderBy(x => x.Name));
        Assert.Empty(defaults.Select(l => l.Id).Intersect(boardLabels.Select(l => l.Id)));
    }

    [SkippableFact]
    public async Task EditingProjectDefaults_LeavesExistingBoardsAlone()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Defaults");

        var bug = (await _admin.GetJson<List<LabelDto>>($"/api/projects/{project.Id}/labels"))
            .First(l => l.Name == "Bug");
        await _admin.PutJson<LabelDto>($"/api/projects/{project.Id}/labels/{bug.Id}", new
        {
            name = "Defect",
            color = "#000001",
            version = bug.Version,
        });

        var boardLabels = await _admin.GetJson<List<LabelDto>>($"/api/boards/{board.Id}/labels");
        Assert.Contains(boardLabels, l => l.Name == "Bug");
        Assert.DoesNotContain(boardLabels, l => l.Name == "Defect");

        // A board created afterwards starts from the edited defaults.
        var later = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Later", alias = "LATER" });
        var laterLabels = await _admin.GetJson<List<LabelDto>>($"/api/boards/{later.Id}/labels");
        Assert.Contains(laterLabels, l => l.Name == "Defect");
    }

    [SkippableFact]
    public async Task ResettingBoardLabels_RestoresTheDefaults_AndKeepsMatchingAssignments()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Reset");

        var labels = await _admin.GetJson<List<LabelDto>>($"/api/boards/{board.Id}/labels");
        var bug = labels.First(l => l.Name == "Bug");
        var feature = labels.First(l => l.Name == "Feature");

        var kept = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Keeps its label", labelId = bug.Id });

        // Rename one default, drop another, and add a label of the board's own.
        await _admin.PutJson<LabelDto>($"/api/boards/{board.Id}/labels/{bug.Id}", new
        {
            name = "Defect",
            color = "#010203",
            version = bug.Version,
        });
        await _admin.DeleteAsync($"/api/boards/{board.Id}/labels/{feature.Id}");
        var invented = await _admin.PostJson<LabelDto>($"/api/boards/{board.Id}/labels", new
        {
            kind = "Task",
            name = "Playtest",
            color = "#123456",
        });
        var orphaned = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Loses its label", labelId = invented.Id });

        var reset = await _admin.PostJson<List<LabelDto>>($"/api/boards/{board.Id}/labels/reset", new { });

        Assert.Equal(7, reset.Count);
        Assert.All(reset, l => Assert.Equal(board.Id, l.BoardId));
        Assert.DoesNotContain(reset, l => l.Name == "Playtest");
        Assert.DoesNotContain(reset, l => l.Name == "Defect");
        Assert.Contains(reset, l => l.Name == "Feature");
        Assert.Equal("#e5484d", reset.First(l => l.Name == "Bug").Color);

        // The renamed copy is restored, not replaced, so its task keeps it; the
        // invented label is gone, and its task is left unlabelled.
        Assert.Equal(bug.Id, (await _admin.GetJson<TaskDto>($"/api/tasks/{kept.Id}")).LabelId);
        Assert.Null((await _admin.GetJson<TaskDto>($"/api/tasks/{orphaned.Id}")).LabelId);
    }

    [SkippableFact]
    public async Task FullHierarchy_CrudRoundTrip()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Round trip");

        var epic = await _admin.PostJson<EpicDto>(
            $"/api/boards/{board.Id}/epics", new { name = "Vertical slice", description = "## Goals" });
        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Player controller", epicId = epic.Id });
        var adHoc = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Fix build script" });
        var subTask = await _admin.PostJson<SubTaskDto>(
            $"/api/tasks/{task.Id}/subtasks", new { name = "Jump physics" });
        var comment = await _admin.PostJson<CommentDto>(
            $"/api/tasks/{task.Id}/comments", new { body = "Ship it" });

        Assert.Null(adHoc.EpicId);
        Assert.Equal(epic.Id, task.EpicId);
        Assert.True(adHoc.Rank > task.Rank);

        var detail = await _admin.GetJson<BoardDetailDto>($"/api/boards/{board.Id}");
        Assert.Single(detail.Epics);
        Assert.Equal(2, detail.Tasks.Count);
        Assert.Equal(7, detail.Labels.Count);
        Assert.Single(detail.Tasks.Single(t => t.Id == task.Id).SubTasks);

        var comments = await _admin.GetJson<List<CommentDto>>($"/api/tasks/{task.Id}/comments");
        Assert.Equal([comment.Id], comments.Select(c => c.Id));
        Assert.Equal("Admin", comments[0].AuthorDisplayName);
        Assert.Equal(subTask.TaskId, task.Id);

        // Board summary list for the project.
        var boards = await _admin.GetJson<List<BoardDto>>($"/api/projects/{project.Id}/boards");
        Assert.Single(boards);
    }

    [SkippableFact]
    public async Task Deadline_DefaultsToNone_AndRoundTrips()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("Deadlines");

        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Ship the demo" });
        Assert.Null(task.DeadlineAt);
        Assert.True(task.CreatedAt > DateTime.UtcNow.AddMinutes(-5));

        // Set a deadline.
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        var updated = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}", new
        {
            name = task.Name,
            deadlineAt = due,
            version = task.Version,
        });
        Assert.Equal(due, updated.DeadlineAt);

        // Clear it again — omitting the field means no deadline, not "keep".
        updated = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}", new
        {
            name = task.Name,
            version = updated.Version,
        });
        Assert.Null(updated.DeadlineAt);
    }

    [SkippableFact]
    public async Task Schedule_SetsStartAndDeadlineAsAPair_AndRoundTrips()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("Schedule");

        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Plan the launch" });
        Assert.Null(task.StartAt);

        var start = new DateOnly(2026, 10, 1);
        var due = new DateOnly(2026, 10, 14);
        var scheduled = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/schedule",
            new { startAt = start, deadlineAt = due, version = task.Version });
        Assert.Equal(start, scheduled.StartAt);
        Assert.Equal(due, scheduled.DeadlineAt);

        var fetched = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal((start, due), (fetched.StartAt, fetched.DeadlineAt));
        var listed = Assert.Single(await _admin.GetJson<List<TaskDto>>($"/api/boards/{board.Id}/tasks"));
        Assert.Equal(start, listed.StartAt);

        // Create takes the pair too.
        var created = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks",
            new { name = "Born scheduled", startAt = start, deadlineAt = due });
        Assert.Equal((start, due), (created.StartAt, created.DeadlineAt));
    }

    [SkippableFact]
    public async Task Schedule_RefusesAStartWithoutADeadline_OrAfterIt()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("BadSched");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Undated" });

        var noDeadline = await _admin.PutAsJsonAsync($"/api/tasks/{task.Id}/schedule",
            new { startAt = new DateOnly(2026, 10, 1), version = task.Version }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, noDeadline.StatusCode);

        var backwards = await _admin.PutAsJsonAsync($"/api/tasks/{task.Id}/schedule",
            new { startAt = new DateOnly(2026, 10, 10), deadlineAt = new DateOnly(2026, 10, 5), version = task.Version },
            TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);

        // Create applies the same two rules.
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync($"/api/boards/{board.Id}/tasks",
            new { name = "x", startAt = new DateOnly(2026, 10, 1) }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync($"/api/boards/{board.Id}/tasks",
            new { name = "x", startAt = new DateOnly(2026, 10, 2), deadlineAt = new DateOnly(2026, 10, 1) },
            TestClients.Json)).StatusCode);

        // A one-day range — start on the deadline — is fine.
        var oneDay = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/schedule",
            new { startAt = new DateOnly(2026, 10, 1), deadlineAt = new DateOnly(2026, 10, 1), version = task.Version });
        Assert.Equal(oneDay.StartAt, oneDay.DeadlineAt);
    }

    /// <summary>
    /// The full-replace PUT can't carry a start, so an older client renaming a
    /// task must not wipe one — but when it takes the deadline away, or moves it
    /// before the start, the start goes with it rather than failing the edit.
    /// </summary>
    [SkippableFact]
    public async Task UpdatingATask_LeavesItsStartAlone_UntilTheDeadlineStopsCoveringIt()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("KeepStart");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Ranged" });
        var start = new DateOnly(2026, 10, 5);
        var due = new DateOnly(2026, 10, 20);
        task = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/schedule",
            new { startAt = start, deadlineAt = due, version = task.Version });

        var renamed = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}",
            new { name = "Renamed", deadlineAt = due, version = task.Version });
        Assert.Equal(start, renamed.StartAt);

        var extended = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}",
            new { name = "Renamed", deadlineAt = due.AddDays(3), version = renamed.Version });
        Assert.Equal(start, extended.StartAt);

        var cleared = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}",
            new { name = "Renamed", version = extended.Version });
        Assert.Null(cleared.DeadlineAt);
        Assert.Null(cleared.StartAt);

        task = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/schedule",
            new { startAt = start, deadlineAt = due, version = cleared.Version });
        var beforeStart = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}",
            new { name = "Renamed", deadlineAt = start.AddDays(-1), version = task.Version });
        Assert.Equal(start.AddDays(-1), beforeStart.DeadlineAt);
        Assert.Null(beforeStart.StartAt);
    }

    [SkippableFact]
    public async Task Schedule_WithAStaleVersion_Is409()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("StaleSched");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Contended" });
        var due = new DateOnly(2026, 10, 20);

        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/schedule",
            new { deadlineAt = due, version = task.Version });
        var stale = await _admin.PutAsJsonAsync($"/api/tasks/{task.Id}/schedule",
            new { startAt = due, deadlineAt = due, version = task.Version }, TestClients.Json);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Null((await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).StartAt);
    }

    [SkippableFact]
    public async Task Schedule_NeedsWriteAccess()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("ReadSched");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Guarded" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members",
            new { email = "schedreader@example.com", role = "Read" });
        var reader = _factory.As("schedreader", "schedreader@example.com");

        var response = await reader.PutAsJsonAsync($"/api/tasks/{task.Id}/schedule",
            new { deadlineAt = new DateOnly(2026, 10, 20), version = task.Version }, TestClients.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task ManualStatus_IsAllowed_WithSubtasks_AndTogglesDoNotDeriveIt()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("Rollup");

        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "With checklist" });
        var s1 = await _admin.PostJson<SubTaskDto>($"/api/tasks/{task.Id}/subtasks", new { name = "one" });
        var s2 = await _admin.PostJson<SubTaskDto>($"/api/tasks/{task.Id}/subtasks", new { name = "two" });

        // Manual status writes are allowed even with sub-tasks — no column is locked.
        var manual = await _admin.PutAsJsonAsync(
            $"/api/tasks/{task.Id}/status", new { status = "Finished" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var current = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal(TaskState.Finished, current.Status);

        // Toggling sub-tasks no longer changes the task's status server-side; the
        // client drives any auto-complete as a separate status call.
        await _admin.PutJson<SubTaskDto>($"/api/subtasks/{s1.Id}/finished", new { isFinished = true });
        await _admin.PutJson<SubTaskDto>($"/api/subtasks/{s2.Id}/finished", new { isFinished = true });
        current = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal(TaskState.Finished, current.Status);

        // And the card can be manually moved back out of Finished.
        var back = await _admin.PutAsJsonAsync(
            $"/api/tasks/{task.Id}/status", new { status = "Unstarted" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        current = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal(TaskState.Unstarted, current.Status);
    }

    [SkippableFact]
    public async Task Images_UploadThenServeAnonymously_AndRejectBadTypes()
    {
        postgres.SkipUnlessDockerAvailable();

        var bytes = PngBytes();
        var post = await UploadImage(bytes, "image/png", "icon.png");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var dto = await post.Content.ReadFromJsonAsync<ImageAssetDto>(TestClients.Json);
        Assert.Equal("image/png", dto!.ContentType);

        // Served anonymously so a plain <img src> resolves without an auth header.
        using var anon = _factory.CreateClient();
        var get = await anon.GetAsync($"/api/images/{dto.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/png", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync());

        // Non-image content types are refused.
        var badPost = await UploadImage(bytes, "text/plain", "x.txt");
        Assert.Equal(HttpStatusCode.BadRequest, badPost.StatusCode);
    }

    /// <summary>
    /// The declared content type is echoed back on every read, so bytes that
    /// don't match it would be served as a type they aren't — the classic route
    /// to getting a scripted document served from the app's own origin.
    /// </summary>
    [SkippableFact]
    public async Task Images_RejectBytesThatContradictTheDeclaredType()
    {
        postgres.SkipUnlessDockerAvailable();

        var html = "<html><script>alert(1)</script></html>"u8.ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UploadImage(html, "image/png", "icon.png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UploadImage(html, "image/jpeg", "icon.jpg")).StatusCode);
    }

    /// <summary>
    /// An SVG is a document, and this one is served from the app's own origin.
    /// The isolation headers are what keep a scripted upload from running there
    /// when somebody opens the image URL directly.
    /// </summary>
    [SkippableFact]
    public async Task Images_AreServedUnderAnIsolationPolicy()
    {
        postgres.SkipUnlessDockerAvailable();

        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        var post = await UploadImage(svg, "image/svg+xml", "icon.svg");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var dto = await post.Content.ReadFromJsonAsync<ImageAssetDto>(TestClients.Json);

        using var anon = _factory.CreateClient();
        var get = await anon.GetAsync($"/api/images/{dto!.Id}");

        var csp = Assert.Single(get.Content.Headers.TryGetValues("Content-Security-Policy", out var contentCsp)
            ? contentCsp
            : get.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("sandbox", csp);
        Assert.Equal("nosniff", Assert.Single(get.Headers.GetValues("X-Content-Type-Options")));
    }

    private async Task<HttpResponseMessage> UploadImage(byte[] bytes, string contentType, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return await _admin.PostAsync("/api/images", form);
    }

    /// <summary>A PNG signature plus a token payload — enough to be a PNG.</summary>
    private static byte[] PngBytes() =>
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [SkippableFact]
    public async Task Avatar_ForUserWithoutPicture_IsAnonymousAnd404()
    {
        postgres.SkipUnlessDockerAvailable();
        var me = await _admin.GetJson<UserDto>("/api/users/me");

        // Served anonymously so a plain <img> resolves (no 401); a user with no
        // stored picture yields 404 so the client falls back to initials.
        using var anon = _factory.CreateClient();
        var resp = await anon.GetAsync($"/api/users/{me.Id}/avatar");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [SkippableFact]
    public async Task EpicDeletion_DetachesTasksToAdHoc()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("Detach");

        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Doomed" });
        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Survivor", epicId = epic.Id });

        var delete = await _admin.DeleteAsync($"/api/epics/{epic.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var survivor = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Null(survivor.EpicId);
    }

    [SkippableFact]
    public async Task ReadMembers_CanRead_ButNotWrite()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Authz");
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "reader@example.com", role = "Read" });

        var reader = _factory.As("reader", "reader@example.com", "Reader");
        // First request provisions the user and activates the pending invite.
        var me = await reader.GetJson<UserDto>("/api/users/me");
        Assert.False(me.IsSuperAdmin);

        var detail = await reader.GetJson<BoardDetailDto>($"/api/boards/{board.Id}");
        Assert.Equal(board.Id, detail.Id);

        var write = await reader.PostAsJsonAsync(
            $"/api/boards/{board.Id}/tasks", new { name = "Denied" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var stranger = _factory.As("stranger", "stranger@example.com");
        var read = await stranger.GetAsync($"/api/boards/{board.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var memberOnlyAudit = await reader.GetAsync($"/api/projects/{project.Id}/audit");
        Assert.Equal(HttpStatusCode.Forbidden, memberOnlyAudit.StatusCode);
    }

    /// <summary>
    /// A board's lifecycle — create, close, reopen, delete — is the super
    /// admin's alone, even against the project's owner; what happens on the
    /// board stays with its writers.
    /// </summary>
    [SkippableFact]
    public async Task BoardLifecycle_IsSuperAdminOnly_ButSettingsStayWithWriters()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Lifecycle");
        var invite = await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "owner@example.com", role = "ReadWrite" });
        await _admin.PutJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members/{invite.Id}", new { role = "ReadWrite", isOwner = true });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "writer@example.com", role = "ReadWrite" });

        var owner = _factory.As("owner", "owner@example.com", "Owner");
        var writer = _factory.As("writer", "writer@example.com", "Writer");
        Assert.False((await owner.GetJson<UserDto>("/api/users/me")).IsSuperAdmin);
        await writer.GetJson<UserDto>("/api/users/me");

        foreach (var member in new[] { owner, writer })
        {
            var create = await member.PostAsJsonAsync(
                $"/api/projects/{project.Id}/boards", new { name = "Denied", alias = "DENIED" }, TestClients.Json);
            var close = await member.PutAsJsonAsync(
                $"/api/boards/{board.Id}/open", new { isOpen = false }, TestClients.Json);
            var delete = await member.DeleteAsync($"/api/boards/{board.Id}");

            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }

        var renamed = await writer.PutJson<BoardDto>($"/api/boards/{board.Id}",
            new { name = "Renamed", alias = board.Alias, version = board.Version });
        Assert.Equal("Renamed", renamed.Name);

        var closed = await _admin.PutJson<BoardDto>($"/api/boards/{board.Id}/open", new { isOpen = false });
        Assert.False(closed.IsOpen);
        var reopened = await _admin.PutJson<BoardDto>($"/api/boards/{board.Id}/open", new { isOpen = true });
        Assert.True(reopened.IsOpen);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/boards/{board.Id}")).StatusCode);

        // The owner policy alongside resolves the project, so a project that
        // isn't there is refused rather than failing on its foreign key.
        var nowhere = await _admin.PostAsJsonAsync(
            $"/api/projects/{Guid.NewGuid()}/boards", new { name = "Nowhere", alias = "NOWHERE" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, nowhere.StatusCode);
    }

    [SkippableFact]
    public async Task SaveEvents_ProduceColumnLevelAuditRows()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Audit");

        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Before rename" });
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}", new
        {
            name = "After rename",
            version = task.Version,
        });

        var audit = await _admin.GetJson<PagedResult<AuditEntryDto>>(
            $"/api/projects/{project.Id}/audit?table=Tasks&pageSize=50");
        Assert.Contains(audit.Items, a =>
            a.RowId == task.Id.ToString() && a.ColumnName == null && a.PreviousValue == null);
        Assert.Contains(audit.Items, a =>
            a.RowId == task.Id.ToString() && a.ColumnName == "Name" && a.PreviousValue == "Before rename");
        Assert.All(audit.Items, a => Assert.NotNull(a.UserId));
    }

    [SkippableFact]
    public async Task StaleVersion_Yields409_FreshVersionSucceeds()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("Concurrency");

        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Contended" });

        // First save bumps xmin.
        var updated = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}", new
        {
            name = "First writer",
            version = task.Version,
        });
        Assert.NotEqual(task.Version, updated.Version);

        // Second writer still holds the original version: 409.
        var stale = await _admin.PutAsJsonAsync($"/api/tasks/{task.Id}", new
        {
            name = "Second writer",
            version = task.Version,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Re-fetch and merge path succeeds.
        var fresh = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        var merged = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}", new
        {
            name = "Second writer merged",
            version = fresh.Version,
        });
        Assert.Equal("Second writer merged", merged.Name);
    }

    [SkippableFact]
    public async Task Labels_EnforceKindAndBoardScope()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Labels");
        var otherBoard = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Other", alias = "OTHER" });

        var otherBoardLabel = await _admin.PostJson<LabelDto>($"/api/boards/{otherBoard.Id}/labels", new
        {
            kind = "Task",
            name = "Local",
            color = "#123456",
        });

        // Another board's label is rejected.
        var rejected = await _admin.PostAsJsonAsync($"/api/boards/{board.Id}/tasks", new
        {
            name = "Bad label",
            labelId = otherBoardLabel.Id,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // So is a project default: it is a template, not a label items can carry.
        var projectDefault = (await _admin.GetJson<List<LabelDto>>($"/api/projects/{project.Id}/labels"))
            .First(l => l.Kind == LabelKind.Task);
        var templateRejected = await _admin.PostAsJsonAsync($"/api/boards/{board.Id}/tasks", new
        {
            name = "Template label",
            labelId = projectDefault.Id,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, templateRejected.StatusCode);

        // A sub-task label can't be used on a task.
        var labels = await _admin.GetJson<List<LabelDto>>($"/api/boards/{board.Id}/labels");
        var subTaskLabel = labels.First(l => l.Kind == LabelKind.SubTask);
        var wrongKind = await _admin.PostAsJsonAsync($"/api/boards/{board.Id}/tasks", new
        {
            name = "Wrong kind",
            labelId = subTaskLabel.Id,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);

        // The matching kind works.
        var taskLabel = labels.First(l => l.Kind == LabelKind.Task);
        var ok = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new
        {
            name = "Labelled",
            labelId = taskLabel.Id,
        });
        Assert.Equal(taskLabel.Id, ok.LabelId);
    }

    [SkippableFact]
    public async Task NonSuperAdmin_CannotCreateProjects()
    {
        postgres.SkipUnlessDockerAvailable();
        var user = _factory.As("plain", "plain@example.com");
        // Member of nothing: the profile is refused and no row is provisioned.
        var me = await user.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);

        var denied = await user.PostAsJsonAsync(
            "/api/projects", new { name = "Nope", alias = "NOPE" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [SkippableFact]
    public async Task Me_RefusesAccesslessAccounts_UntilTheyAreInvited()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, _) = await CreateProjectWithBoard("Gate");

        // A valid token is not enough: member of nothing, not super admin -> 403.
        var joiner = _factory.As("joiner", "joiner@example.com", "Joiner");
        var refused = await joiner.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // An invite opens the door: the next sign-in provisions the row and
        // activates the pending invite in one pass.
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "joiner@example.com", role = "Read" });
        var me = await joiner.GetJson<UserDto>("/api/users/me");
        Assert.Equal("joiner@example.com", me.Email);
        Assert.False(me.IsSuperAdmin);
    }

    [SkippableFact]
    public async Task UninvitedSignIn_LeavesNoUserRow()
    {
        postgres.SkipUnlessDockerAvailable();

        var drifter = _factory.As("drifter", "drifter@example.com", "Drifter");
        var refused = await drifter.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var options = new DbContextOptionsBuilder<SoloDbContext>()
            .UseNpgsql(_connectionString).Options;
        await using var db = new SoloDbContext(options);
        Assert.False(await db.Users.AnyAsync(u => u.Email == "drifter@example.com"));
    }

    // -- Moving a task to another board --------------------------------------

    [SkippableFact]
    public async Task MovingATask_ReseatsWhatTheBoardOwned_AndKeepsEverythingElse()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, home) = await CreateProjectWithBoard("Transfer");
        var away = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Away", alias = "AWAY" });
        var awayEpic = await _admin.PostJson<EpicDto>($"/api/boards/{away.Id}/epics", new { name = "Slice" });

        // Away already has a task, so a serial carried over from home would
        // collide rather than merely look wrong.
        await _admin.PostJson<TaskDto>($"/api/boards/{away.Id}/tasks", new { name = "Sitting there" });

        var homeLabels = await _admin.GetJson<List<LabelDto>>($"/api/boards/{home.Id}/labels");
        var homeTaskLabel = homeLabels.First(l => l.Kind == LabelKind.Task);
        var homeSubLabel = homeLabels.First(l => l.Kind == LabelKind.SubTask);

        var task = await _admin.PostJson<TaskDto>($"/api/boards/{home.Id}/tasks", new
        {
            name = "Travelling task",
            labelId = homeTaskLabel.Id,
        });
        var subTask = await _admin.PostJson<SubTaskDto>(
            $"/api/tasks/{task.Id}/subtasks", new { name = "Packed", labelId = homeSubLabel.Id });
        await _admin.PostJson<CommentDto>($"/api/tasks/{task.Id}/comments", new { body = "Wrong board" });
        var me = await _admin.GetJson<UserDto>("/api/users/me");
        await _admin.PutJson<TaskDto>(
            $"/api/tasks/{task.Id}/assignees", new { userIds = new[] { me.Id } });
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/status", new { status = "InProgress" });

        var moved = await _admin.PutJson<TaskDto>(
            $"/api/tasks/{task.Id}/board", new { boardId = away.Id, epicId = awayEpic.Id });

        Assert.Equal(task.Id, moved.Id);
        Assert.Equal(away.Id, moved.BoardId);
        Assert.Equal(awayEpic.Id, moved.EpicId);
        Assert.Equal(2, moved.Number);                    // away's sequence, not home's
        Assert.Equal(TaskState.InProgress, moved.Status); // columns are the same everywhere

        // Board-owned labels are re-resolved to away's copies of the same defaults.
        var awayLabels = await _admin.GetJson<List<LabelDto>>($"/api/boards/{away.Id}/labels");
        var awayTaskLabel = awayLabels.First(l => l.Kind == LabelKind.Task && l.Name == homeTaskLabel.Name);
        var awaySubLabel = awayLabels.First(l => l.Kind == LabelKind.SubTask && l.Name == homeSubLabel.Name);
        Assert.Equal(awayTaskLabel.Id, moved.LabelId);
        Assert.NotEqual(homeTaskLabel.Id, moved.LabelId);
        Assert.Equal(awaySubLabel.Id, moved.SubTasks.Single().LabelId);

        // Everything hanging off the task came along.
        Assert.Equal([subTask.Id], moved.SubTasks.Select(s => s.Id));
        Assert.Equal([me.Id], moved.AssigneeIds);
        Assert.Single(await _admin.GetJson<List<CommentDto>>($"/api/tasks/{task.Id}/comments"));

        // And each board's payload agrees on where it lives now.
        var homeDetail = await _admin.GetJson<BoardDetailDto>($"/api/boards/{home.Id}");
        var awayDetail = await _admin.GetJson<BoardDetailDto>($"/api/boards/{away.Id}");
        Assert.DoesNotContain(homeDetail.Tasks, t => t.Id == task.Id);
        Assert.Contains(awayDetail.Tasks, t => t.Id == task.Id);
        Assert.True(awayDetail.Tasks.Single(t => t.Id == task.Id).Rank >
                    awayDetail.Tasks.Single(t => t.Id != task.Id).Rank);
    }

    [SkippableFact]
    public async Task MovingATask_ClearsALabelTheDestinationHasNoAnswerFor()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, home) = await CreateProjectWithBoard("Orphan");
        var away = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Away", alias = "AWAY" });

        // A label home invented for itself: away has never heard of it.
        var local = await _admin.PostJson<LabelDto>($"/api/boards/{home.Id}/labels", new
        {
            kind = "Task",
            name = "Playtest blocker",
            color = "#123456",
        });
        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{home.Id}/tasks", new { name = "Unlabelled soon", labelId = local.Id });

        var moved = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/board", new { boardId = away.Id });

        Assert.Null(moved.LabelId);
        Assert.Null(moved.EpicId); // no epic named: the ad-hoc band
    }

    /// <summary>
    /// A task's serial number is how prose names it, and a move changes that
    /// number. Every reference in the project follows the task; everything else
    /// in the same text is left exactly as its author wrote it.
    /// </summary>
    [SkippableFact]
    public async Task MovingATask_CarriesTheReferencesToItAlong()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, home) = await CreateProjectWithBoard("Refs");
        var away = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Away", alias = "AWAY" });

        var task = await _admin.PostJson<TaskDto>($"/api/boards/{home.Id}/tasks", new { name = "Referenced" });
        var bystander = await _admin.PostJson<TaskDto>($"/api/boards/{home.Id}/tasks", new { name = "Bystander" });
        Assert.Equal(1, task.Number);
        Assert.Equal(2, bystander.Number);

        // The three places a reference can live, each also naming something the
        // move must not touch: the bystander task, and a lower-case mention of it.
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{away.Id}/epics", new
        {
            name = "Slice",
            description = "Blocked by ||REFS-REFS-1||, not ||REFS-REFS-2||.",
        });
        await _admin.PutJson<TaskDto>($"/api/tasks/{bystander.Id}", new
        {
            name = bystander.Name,
            description = "See ||refs-refs-1|| and ||refs-refs-2||.",
            version = bystander.Version,
        });
        var comment = await _admin.PostJson<CommentDto>($"/api/tasks/{bystander.Id}/comments", new
        {
            body = "||REFS-REFS-1|| twice: ||REFS-REFS-1||. Not a reference: |REFS-REFS-1|.",
        });

        var moved = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/board", new { boardId = away.Id });
        Assert.Equal(1, moved.Number); // away's own sequence starts over

        // Every reference to the moved task now names where it actually lives.
        var movedEpic = (await _admin.GetJson<BoardDetailDto>($"/api/boards/{away.Id}"))
            .Epics.Single(e => e.Id == epic.Id);
        Assert.Equal("Blocked by ||REFS-AWAY-1||, not ||REFS-REFS-2||.", movedEpic.Description);

        // Case and padding are how it was typed, not what it means: the reference
        // to the moved task is caught, the bystander's is left as written.
        var movedTask = await _admin.GetJson<TaskDto>($"/api/tasks/{bystander.Id}");
        Assert.Equal("See ||REFS-AWAY-1|| and ||refs-refs-2||.", movedTask.Description);

        var comments = await _admin.GetJson<List<CommentDto>>($"/api/tasks/{bystander.Id}/comments");
        Assert.Equal(
            "||REFS-AWAY-1|| twice: ||REFS-AWAY-1||. Not a reference: |REFS-REFS-1|.",
            comments.Single(c => c.Id == comment.Id).Body);
    }

    [SkippableFact]
    public async Task MovingATask_LeavesProseInOtherProjectsAlone()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, home) = await CreateProjectWithBoard("Fences");
        var away = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Away", alias = "AWAY" });
        var (_, elsewhere) = await CreateProjectWithBoard("Bystand");

        var task = await _admin.PostJson<TaskDto>($"/api/boards/{home.Id}/tasks", new { name = "Referenced" });
        var outsider = await _admin.PostJson<TaskDto>(
            $"/api/boards/{elsewhere.Id}/tasks", new { name = "Outsider" });
        await _admin.PutJson<TaskDto>($"/api/tasks/{outsider.Id}", new
        {
            name = outsider.Name,
            description = "Cross-project mention of ||FENCES-FENCES-1||.",
            version = outsider.Version,
        });

        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/board", new { boardId = away.Id });

        // The sweep stays inside the project the caller was authorized for, so a
        // cross-project mention goes stale rather than being edited from outside.
        var untouched = await _admin.GetJson<TaskDto>($"/api/tasks/{outsider.Id}");
        Assert.Equal("Cross-project mention of ||FENCES-FENCES-1||.", untouched.Description);
    }

    [SkippableFact]
    public async Task MovingATask_RefusesAnyDestinationOutsideItsProject()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, home) = await CreateProjectWithBoard("Fenced");
        var (_, elsewhere) = await CreateProjectWithBoard("Neighbour");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{home.Id}/tasks", new { name = "Stays put" });

        // Another project's board — even for the super admin, who can see both.
        var crossProject = await _admin.PutAsJsonAsync(
            $"/api/tasks/{task.Id}/board", new { boardId = elsewhere.Id }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);

        // A board that doesn't exist reads the same, and says nothing more.
        var unknown = await _admin.PutAsJsonAsync(
            $"/api/tasks/{task.Id}/board", new { boardId = Guid.NewGuid() }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Its own board is not a move.
        var itself = await _admin.PutAsJsonAsync(
            $"/api/tasks/{task.Id}/board", new { boardId = home.Id }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, itself.StatusCode);

        var unmoved = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal(home.Id, unmoved.BoardId);
        Assert.Equal(1, unmoved.Number);
    }

    [SkippableFact]
    public async Task MovingATask_RejectsAnEpicFromAnyOtherBoard()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, home) = await CreateProjectWithBoard("Lanes");
        var away = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Away", alias = "AWAY" });
        var homeEpic = await _admin.PostJson<EpicDto>($"/api/boards/{home.Id}/epics", new { name = "Home lane" });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{home.Id}/tasks", new { name = "Lane bound" });

        var wrongLane = await _admin.PutAsJsonAsync(
            $"/api/tasks/{task.Id}/board", new { boardId = away.Id, epicId = homeEpic.Id }, TestClients.Json);

        Assert.Equal(HttpStatusCode.BadRequest, wrongLane.StatusCode);
        Assert.Equal(home.Id, (await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).BoardId);
    }

    [SkippableFact]
    public async Task MovingATask_NeedsWriteAccess()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, home) = await CreateProjectWithBoard("Readers");
        var away = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Away", alias = "AWAY" });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{home.Id}/tasks", new { name = "Look, don't touch" });

        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "reader@example.com", role = "Read" });
        var reader = _factory.As("reader", "reader@example.com", "Reader");

        var denied = await reader.PutAsJsonAsync(
            $"/api/tasks/{task.Id}/board", new { boardId = away.Id }, TestClients.Json);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // -- Aliases and serial numbers ------------------------------------------

    [SkippableFact]
    public async Task TaskNumbers_IncrementPerBoard_AndSurviveDeletion()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Serials");

        var first = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "one" });
        var second = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "two" });
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);

        // Numbers are never reused: delete #2, the next task is #3.
        await _admin.DeleteAsync($"/api/tasks/{second.Id}");
        var third = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "three" });
        Assert.Equal(3, third.Number);

        // A second board numbers independently.
        var other = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Other", alias = "OTHER" });
        var fresh = await _admin.PostJson<TaskDto>($"/api/boards/{other.Id}/tasks", new { name = "reset" });
        Assert.Equal(1, fresh.Number);
    }

    [SkippableFact]
    public async Task Aliases_AreUniquePerSiblingSet_AndNormalizedToUpper()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Unique");

        // A duplicate project alias is refused, whatever case it arrives in.
        var duplicate = await _admin.PostAsJsonAsync(
            "/api/projects", new { name = "Copycat", alias = "UNIQUE" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // A duplicate board alias inside the same project is refused …
        var boardDuplicate = await _admin.PostAsJsonAsync(
            $"/api/projects/{project.Id}/boards", new { name = "Copy", alias = board.Alias }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Conflict, boardDuplicate.StatusCode);

        // … but another project can reuse it freely.
        var second = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Second", alias = "SECOND" });
        var reused = await _admin.PostJson<BoardDto>(
            $"/api/projects/{second.Id}/boards", new { name = "Reuse", alias = board.Alias });
        Assert.Equal(board.Alias, reused.Alias);

        // Lower-case input is stored upper-case.
        Assert.Equal("UNIQUE", project.Alias);
    }

    [SkippableFact]
    public async Task ByAliasRoutes_ResolveCaseInsensitively_AndComposeSerialData()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Lookup");
        await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "numbered" });

        var byAlias = await _admin.GetJson<ProjectDto>($"/api/projects/by-alias/{project.Alias.ToLower()}");
        Assert.Equal(project.Id, byAlias.Id);

        var detail = await _admin.GetJson<BoardDetailDto>(
            $"/api/projects/by-alias/{project.Alias.ToLower()}/boards/{board.Alias.ToLower()}");
        Assert.Equal(board.Id, detail.Id);
        Assert.Equal(project.Alias, detail.ProjectAlias);
        Assert.Equal(1, detail.Tasks.Single().Number);

        // A member-less stranger is refused through the alias route too.
        var stranger = _factory.As("stranger2", "stranger2@example.com");
        var denied = await stranger.GetAsync($"/api/projects/by-alias/{project.Alias}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [SkippableFact]
    public async Task ReadOnlyMembers_CanComment_ButOnlyOnTheirOwnBehalf()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Comments");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Discuss" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "lurker@example.com", role = "Read" });

        var lurker = _factory.As("lurker", "lurker@example.com", "Lurker");
        await lurker.GetJson<UserDto>("/api/users/me");

        // Read-only members join the conversation …
        var comment = await lurker.PostJson<CommentDto>(
            $"/api/tasks/{task.Id}/comments", new { body = "My two cents" });
        var edited = await lurker.PutJson<CommentDto>(
            $"/api/comments/{comment.Id}", new { body = "Revised cents", version = comment.Version });
        Assert.Equal("Revised cents", edited.Body);

        // … but still cannot touch the board itself, or someone else's comment.
        var write = await lurker.PostAsJsonAsync(
            $"/api/boards/{board.Id}/tasks", new { name = "Denied" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var adminComment = await _admin.PostJson<CommentDto>(
            $"/api/tasks/{task.Id}/comments", new { body = "Admin says" });
        var foreignEdit = await lurker.PutAsJsonAsync(
            $"/api/comments/{adminComment.Id}",
            new { body = "Hijacked", version = adminComment.Version }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, foreignEdit.StatusCode);
    }

    /// <summary>
    /// Closing a board freezes the conversation on it, for the admin who owns the
    /// project as much as for anyone else. Reopening thaws it again.
    /// </summary>
    [SkippableFact]
    public async Task ClosedBoards_TakeNoComments_OnEitherTasksOrEpics()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoard("Frozen");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Discuss" });
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Ship it" });
        var comment = await _admin.PostJson<CommentDto>(
            $"/api/tasks/{task.Id}/comments", new { body = "Said while open" });

        await _admin.PutJson<BoardDto>($"/api/boards/{board.Id}/open", new { isOpen = false });

        var onTask = await _admin.PostAsJsonAsync(
            $"/api/tasks/{task.Id}/comments", new { body = "Too late" }, TestClients.Json);
        var onEpic = await _admin.PostAsJsonAsync(
            $"/api/epics/{epic.Id}/comments", new { body = "Too late" }, TestClients.Json);
        var edit = await _admin.PutAsJsonAsync(
            $"/api/comments/{comment.Id}",
            new { body = "Revised", version = comment.Version }, TestClients.Json);
        var delete = await _admin.DeleteAsync($"/api/comments/{comment.Id}");

        Assert.Equal(HttpStatusCode.Conflict, onTask.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, onEpic.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // What was said before the board closed is still there to read.
        var thread = await _admin.GetJson<List<CommentDto>>($"/api/tasks/{task.Id}/comments");
        Assert.Equal([comment.Id], thread.Select(c => c.Id));

        // Reopening is the way back: the same call now lands.
        await _admin.PutJson<BoardDto>($"/api/boards/{board.Id}/open", new { isOpen = true });
        var reopened = await _admin.PostJson<CommentDto>(
            $"/api/tasks/{task.Id}/comments", new { body = "Back on" });
        Assert.Equal("Back on", reopened.Body);
    }

    // -- Assignees -----------------------------------------------------------

    /// <summary>
    /// The PUT answers with the shape a later GET agrees with. It used to append each
    /// new assignment to the navigation on top of EF's relationship fixup, which had
    /// already put it there, so every newly added assignee came back listed twice.
    /// </summary>
    [SkippableFact]
    public async Task SettingAssignees_ListsEachOneOnce_OnTasksAndEpics()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoard("Assignees");
        var me = await _admin.GetJson<UserDto>("/api/users/me");
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "pair@example.com", role = "ReadWrite" });
        var pair = await _factory.As("pair", "pair@example.com", "Pair").GetJson<UserDto>("/api/users/me");

        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Assign me" });
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Assign us" });

        // One brand-new assignment: the case the duplicate was first seen on.
        var oneTask = await _admin.PutJson<TaskDto>(
            $"/api/tasks/{task.Id}/assignees", new { userIds = new[] { me.Id } });
        var oneEpic = await _admin.PutJson<EpicDto>(
            $"/api/epics/{epic.Id}/assignees", new { userIds = new[] { me.Id } });
        Assert.Equal([me.Id], oneTask.AssigneeIds);
        Assert.Equal([me.Id], oneEpic.AssigneeIds);

        // Adding one beside an assignee that is already there exercises both paths at once.
        Guid[] both = [me.Id, pair.Id];
        var bothTask = await _admin.PutJson<TaskDto>(
            $"/api/tasks/{task.Id}/assignees", new { userIds = both });
        var bothEpic = await _admin.PutJson<EpicDto>(
            $"/api/epics/{epic.Id}/assignees", new { userIds = both });
        Assert.Equal(both.Order(), bothTask.AssigneeIds.Order());
        Assert.Equal(both.Order(), bothEpic.AssigneeIds.Order());

        // Dropping one leaves the other named once, not re-added.
        var droppedTask = await _admin.PutJson<TaskDto>(
            $"/api/tasks/{task.Id}/assignees", new { userIds = new[] { pair.Id } });
        var droppedEpic = await _admin.PutJson<EpicDto>(
            $"/api/epics/{epic.Id}/assignees", new { userIds = new[] { pair.Id } });
        Assert.Equal([pair.Id], droppedTask.AssigneeIds);
        Assert.Equal([pair.Id], droppedEpic.AssigneeIds);

        // What was stored all along: the response was the only thing ever wrong.
        var storedTask = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        var storedEpic = (await _admin.GetJson<List<EpicDto>>($"/api/boards/{board.Id}/epics"))
            .Single(e => e.Id == epic.Id);
        Assert.Equal([pair.Id], storedTask.AssigneeIds);
        Assert.Equal([pair.Id], storedEpic.AssigneeIds);
    }
}
