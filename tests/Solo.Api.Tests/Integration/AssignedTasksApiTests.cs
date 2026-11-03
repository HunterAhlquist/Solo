using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solo.Data;
using Solo.Extensions;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// One person's work across projects (contract 1.13): what the Your Tasks
/// dashboard reads, what an extension reads through ISoloQuery, and the user
/// topic that tells both when it changed.
/// </summary>
[Collection("postgres")]
public sealed class AssignedTasksApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;
    private Guid _bobId;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("assigned_tasks"), [SoloApiFactory.FixtureDirectory]);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
        _bob = _factory.As("bob", "bob@example.com", "Bob");
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(ProjectDto Project, BoardDto Board)> ProjectWithBobAsync(string name, string alias)
    {
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name, alias });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members",
            new { email = "bob@example.com", role = "ReadWrite" });
        var board = await _admin.PostJson<BoardDto>($"/api/projects/{project.Id}/boards",
            new { name = $"{name} board", alias });
        // Membership is pending until Bob signs in; his first call provisions him.
        _bobId = (await _bob.GetJson<UserDto>("/api/users/me")).Id;
        return (project, board);
    }

    private async Task<TaskDto> TaskForBobAsync(BoardDto board, string name)
    {
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name });
        return await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { _bobId } });
    }

    [SkippableFact]
    public async Task MyTasks_SpanProjects_AndLeaveOutClosedBoards_OthersWork_AndProjectsLeft()
    {
        postgres.SkipUnlessDockerAvailable();
        var (one, oneBoard) = await ProjectWithBobAsync("One", "ONE");
        var (two, twoBoard) = await ProjectWithBobAsync("Two", "TWO");
        var closed = await _admin.PostJson<BoardDto>($"/api/projects/{one.Id}/boards", new { name = "Closed", alias = "CLOSED" });

        var first = await TaskForBobAsync(oneBoard, "First");
        var second = await TaskForBobAsync(twoBoard, "Second");
        var shelved = await TaskForBobAsync(closed, "Shelved");
        await _admin.PutJson<BoardDto>($"/api/boards/{closed.Id}/open", new { isOpen = false });
        await _admin.PostJson<TaskDto>($"/api/boards/{oneBoard.Id}/tasks", new { name = "Nobody's" });

        var mine = await _bob.GetJson<AssignedTasksDto>("/api/users/me/tasks");
        Assert.Equal(new[] { first.Id, second.Id }.Order(), mine.Tasks.Select(t => t.Id).Order());
        Assert.DoesNotContain(shelved.Id, mine.Tasks.Select(t => t.Id));
        Assert.Equal(new[] { "One", "Two" }, mine.Projects.Select(p => p.Name));
        Assert.Equal(new[] { oneBoard.Id, twoBoard.Id }.Order(), mine.Boards.Select(b => b.Id).Order());
        Assert.All(mine.Labels, l => Assert.Contains(l.BoardId, mine.Boards.Select(b => (Guid?)b.Id)));
        Assert.Equal(new[] { "admin@example.com", "bob@example.com" }, mine.Members.Select(m => m.Email).Order());

        // The admin assigned nothing to themself, and a stranger belongs nowhere.
        Assert.Empty((await _admin.GetJson<AssignedTasksDto>("/api/users/me/tasks")).Tasks);
        var stranger = _factory.As("stranger", "stranger@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/users/me/tasks")).StatusCode);

        // Shown the door from Two: its task goes with the membership, assignment or not.
        var members = await _admin.GetJson<List<ProjectMemberDto>>($"/api/projects/{two.Id}/members");
        var bobInTwo = members.Single(m => m.Email == "bob@example.com");
        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.DeleteAsync($"/api/projects/{two.Id}/members/{bobInTwo.Id}")).StatusCode);
        mine = await _bob.GetJson<AssignedTasksDto>("/api/users/me/tasks");
        Assert.Equal([first.Id], mine.Tasks.Select(t => t.Id));
        Assert.Equal(["One"], mine.Projects.Select(p => p.Name));
    }

    [SkippableFact]
    public async Task FinishedWork_StaysTwelveDays_ThenDropsOff_AndFinishingStampsTheTime()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await ProjectWithBobAsync("Done", "DONE");
        var open = await TaskForBobAsync(board, "Still going");
        var recent = await TaskForBobAsync(board, "Just shipped");
        var old = await TaskForBobAsync(board, "Long shipped");
        var before = DateTime.UtcNow;
        await _admin.PutJson<TaskDto>($"/api/tasks/{recent.Id}/status", new { status = "Finished" });
        await _admin.PutJson<TaskDto>($"/api/tasks/{old.Id}/status", new { status = "Finished" });

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
            var stamped = await db.Tasks.Where(t => t.Id == recent.Id).Select(t => t.FinishedAt).SingleAsync();
            Assert.NotNull(stamped);
            Assert.InRange(stamped.Value, before, DateTime.UtcNow);

            // A day past the line, as if it had been finished then.
            await db.Tasks.Where(t => t.Id == old.Id).ExecuteUpdateAsync(u => u.SetProperty(
                t => t.FinishedAt, DateTime.UtcNow - AssignedTasks.FinishedKeptFor - TimeSpan.FromDays(1)));
        }

        var mine = await _bob.GetJson<AssignedTasksDto>("/api/users/me/tasks");
        Assert.Equal(new[] { open.Id, recent.Id }.Order(), mine.Tasks.Select(t => t.Id).Order());
        var viaExtension = await _bob.GetJson<AssignedTasksDto>("/api/ext/fixture/api/tasks/my-tasks");
        Assert.Equal(mine.Tasks.Select(t => t.Id).Order(), viaExtension.Tasks.Select(t => t.Id).Order());

        // Reopened, it is current work again; finished anew, the clock restarts.
        await _admin.PutJson<TaskDto>($"/api/tasks/{old.Id}/status", new { status = "InProgress" });
        Assert.Contains(old.Id, (await _bob.GetJson<AssignedTasksDto>("/api/users/me/tasks")).Tasks.Select(t => t.Id));
        await _admin.PutJson<TaskDto>($"/api/tasks/{old.Id}/status", new { status = "Finished" });
        Assert.Contains(old.Id, (await _bob.GetJson<AssignedTasksDto>("/api/users/me/tasks")).Tasks.Select(t => t.Id));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
            Assert.Null(await db.Tasks.Where(t => t.Id == open.Id).Select(t => t.FinishedAt).SingleAsync());
        }
    }

    [SkippableFact]
    public async Task MyTasks_CarryTheEpicsAndGroupsTheTasksSitUnder()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await ProjectWithBobAsync("Epics", "EPICS");
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Act One" });
        var grouped = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Boss", groupId = group.Id });
        var loose = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Odd jobs" });
        await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Nobody's epic" });

        async Task<TaskDto> InEpicAsync(EpicDto epic, string name)
        {
            var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name, epicId = epic.Id });
            return await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { _bobId } });
        }

        await InEpicAsync(grouped, "Phase two");
        await InEpicAsync(loose, "Sweep");
        await TaskForBobAsync(board, "Ad hoc");

        var mine = await _bob.GetJson<AssignedTasksDto>("/api/users/me/tasks");
        Assert.Equal(new[] { grouped.Id, loose.Id }.Order(), mine.Epics!.Select(e => e.Id).Order());
        Assert.Equal(group.Id, mine.Epics!.Single(e => e.Id == grouped.Id).GroupId);
        Assert.Equal([group.Id], mine.EpicGroups!.Select(g => g.Id));
    }

    [SkippableFact]
    public async Task AnExtension_ReadsTheSameWork_ScopedToItsCaller()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await ProjectWithBobAsync("Ext", "EXT");
        var task = await TaskForBobAsync(board, "Through the seam");

        var viaExtension = await _bob.GetJson<AssignedTasksDto>("/api/ext/fixture/api/tasks/my-tasks");
        Assert.Equal([task.Id], viaExtension.Tasks.Select(t => t.Id));
        Assert.Equal([board.Id], viaExtension.Boards.Select(b => b.Id));

        var anonymous = await _factory.CreateClient().GetJson<AssignedTasksDto>("/api/ext/fixture/api/tasks/my-tasks");
        Assert.Empty(anonymous.Tasks);
        Assert.Empty(anonymous.Projects);
    }

    [SkippableFact]
    public async Task TheAssignedTasksTopic_NudgesAssignees_OnAssign_Edit_Unassign_AndDelete()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await ProjectWithBobAsync("Live", "LIVE");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Watch me" });

        await using var bob = Connect("bob", "bob@example.com", "Bob");
        await using var admin = Connect("admin", "admin@example.com", "Admin");
        var heard = new List<TopicMessage>();
        var adminHeard = false;
        bob.On<TopicMessage>(nameof(IBoardClient.TopicMessage), m => { lock (heard) heard.Add(m); });
        admin.On<TopicMessage>(nameof(IBoardClient.TopicMessage), _ => adminHeard = true);
        foreach (var connection in new[] { bob, admin })
        {
            await connection.StartAsync();
            await connection.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), BoardHubConstants.AssignedTasksTopic);
        }

        async Task<TopicMessage> NextAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (heard)
                {
                    if (heard.Count >= count) return heard[count - 1];
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"Bob heard {heard.Count} nudges, not {count}.");
        }

        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { _bobId } });
        var assigned = await NextAsync(1);
        Assert.Equal((Guid.Empty, BoardHubConstants.AssignedTasksTopic, EntityTypes.Task, task.Id.ToString()),
            (assigned.ProjectId, assigned.Topic, assigned.Kind, assigned.Data));

        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/status", new { status = "InProgress" });
        Assert.Equal(EntityTypes.Task, (await NextAsync(2)).Kind);

        var sub = await _admin.PostJson<SubTaskDto>($"/api/tasks/{task.Id}/subtasks", new { name = "Step" });
        Assert.Equal((EntityTypes.SubTask, sub.Id.ToString()), ((await NextAsync(3)).Kind, (await NextAsync(3)).Data));
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/subtasks/{sub.Id}")).StatusCode);
        Assert.Equal((EntityTypes.SubTask, sub.Id.ToString()), ((await NextAsync(4)).Kind, (await NextAsync(4)).Data));

        // Taken off the task: the board broadcast no longer reaches him, this does.
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = Array.Empty<Guid>() });
        Assert.Equal(task.Id.ToString(), (await NextAsync(5)).Data);

        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { _bobId } });
        await NextAsync(6);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/tasks/{task.Id}")).StatusCode);
        Assert.Equal(task.Id.ToString(), (await NextAsync(7)).Data);

        // The admin's own topic stays quiet: none of this was their work, and
        // the hub never crosses one person's topic into another's.
        await Task.Delay(300);
        Assert.False(adminHeard, "the admin heard nudges meant for Bob");

        // An epic or group can be named above anyone's card on the board, so a
        // change to one reaches every member, Bob included.
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Arc" });
        Assert.Equal((EntityTypes.Epic, epic.Id.ToString()), ((await NextAsync(8)).Kind, (await NextAsync(8)).Data));
    }

    private HubConnection Connect(string sub, string email, string name) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubConstants.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.SubHeader] = sub;
                options.Headers[TestAuthHandler.EmailHeader] = email;
                options.Headers[TestAuthHandler.NameHeader] = name;
            })
            .Build();
}
