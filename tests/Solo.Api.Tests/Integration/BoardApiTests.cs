using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("board_api"));
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
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = $"{name} board" });
        return (project, board);
    }

    [SkippableFact]
    public async Task ProjectCreation_SeedsDefaultLabels()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, _) = await CreateProjectWithBoard("Seeds");

        var labels = await _admin.GetJson<List<LabelDto>>($"/api/projects/{project.Id}/labels");
        Assert.Equal(7, labels.Count);
        Assert.Contains(labels, l => l.Kind == LabelKind.Task && l.Name == "Bug");
        Assert.Contains(labels, l => l.Kind == LabelKind.SubTask && l.Name == "Writing");
        Assert.All(labels, l => Assert.Null(l.BoardId));
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

        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "icon.png");

        var post = await _admin.PostAsync("/api/images", form);
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
        using var badForm = new MultipartFormDataContent();
        var badFile = new ByteArrayContent(bytes);
        badFile.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        badForm.Add(badFile, "file", "x.txt");
        var badPost = await _admin.PostAsync("/api/images", badForm);
        Assert.Equal(HttpStatusCode.BadRequest, badPost.StatusCode);
    }

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
            $"/api/projects/{project.Id}/boards", new { name = "Other" });

        var boardLocal = await _admin.PostJson<LabelDto>($"/api/projects/{project.Id}/labels", new
        {
            boardId = otherBoard.Id,
            kind = "Task",
            name = "Local",
            color = "#123456",
        });

        // Board-local label from another board is rejected.
        var rejected = await _admin.PostAsJsonAsync($"/api/boards/{board.Id}/tasks", new
        {
            name = "Bad label",
            labelId = boardLocal.Id,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // A sub-task label can't be used on a task.
        var labels = await _admin.GetJson<List<LabelDto>>($"/api/projects/{project.Id}/labels");
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
        await user.GetJson<UserDto>("/api/users/me");

        var denied = await user.PostAsJsonAsync("/api/projects", new { name = "Nope" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
