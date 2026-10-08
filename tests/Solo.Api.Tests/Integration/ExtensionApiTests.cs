using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solo.Data;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The extension system end to end, against a real package discovered on disk
/// and a real Postgres.
/// </summary>
[Collection("postgres")]
public sealed class ExtensionApiTests(PostgresFixture postgres) : IAsyncLifetime
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

        _connectionString = await postgres.CreateDatabaseAsync("extensions");
        _factory = new SoloApiFactory(_connectionString, [SoloApiFactory.FixtureDirectory]);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Authorization. Written first on purpose: an extension that can read rows
    // the caller may not is the failure that matters most, and ISoloQuery
    // exists specifically to make it impossible.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ExtensionQuery_ReturnsNothing_ForAProjectTheCallerCannotRead()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Private", alias = "PRIV" });
        await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Main", alias = "MAIN" });

        // The admin is a member and sees the board through the extension.
        var visibleToAdmin = await _admin.GetJson<List<Guid>>(
            $"/api/ext/fixture/api/tasks/visible-boards/{project.Id}");
        Assert.Single(visibleToAdmin);

        // A signed-in stranger is a member of nothing. The extension asks the
        // same question and must get an empty list, not rows.
        var stranger = _factory.As("stranger", "stranger@example.com");
        var visibleToStranger = await stranger.GetJson<List<Guid>>(
            $"/api/ext/fixture/api/tasks/visible-boards/{project.Id}");
        Assert.Empty(visibleToStranger);
    }

    /// <summary>
    /// The endpoints that boot the client half are authenticated, like anything
    /// else on the fallback policy.
    /// </summary>
    /// <remarks>
    /// They were briefly anonymous, because the client used to fetch them before
    /// it had a credential to send. Nothing needs that: <c>ExtensionAccess</c> is
    /// <c>Read | Write | Owner</c> — all levels of <em>project</em> access — so
    /// no manifest can declare a page that renders before sign-in, and what the
    /// anonymous form published was the instance's extension inventory and a set
    /// of decompilable assemblies.
    /// </remarks>
    [SkippableFact]
    public async Task TheClientInventoryAndAssemblies_AreNotPublic()
    {
        postgres.SkipUnlessDockerAvailable();

        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/extensions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/extensions/fixture/client/Anything.dll")).StatusCode);

        // 401 before 404: an anonymous caller cannot probe which ids exist.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/extensions/no-such-extension/client/Anything.dll")).StatusCode);

        // A Solo user gets the inventory — any user, since an extension's own
        // endpoints do their own authorization. A valid sign-in nobody invited
        // is not one.
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/extensions")).StatusCode);
        var stranger = _factory.As("stranger", "stranger@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/extensions")).StatusCode);
    }

    // -----------------------------------------------------------------------
    // Credentials an extension issues (contract 1.7)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Signing someone out everywhere reaches each extension's credentials, and
    /// one that can't revoke its own is reported rather than hidden: an admin
    /// cutting off a leaked credential has to know if one survived.
    /// </summary>
    [SkippableFact]
    public async Task SignOutEverywhere_ReachesExtensions_AndReportsOneThatFails()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Creds", alias = "CREDS" });
        foreach (var email in new[] { "stuck@example.com", "fine@example.com" })
        {
            await _admin.PostJson<ProjectMemberDto>(
                $"/api/projects/{project.Id}/members", new { email, role = "Read" });
        }

        // The fixture's issuer fails for this subject (FixtureCredentialIssuer).
        var stuck = _factory.As("unrevocable", "stuck@example.com");
        var stuckUser = await stuck.GetJson<UserDto>("/api/users/me");
        var session = await stuck.PostJson<SessionDto>("/api/auth/sessions", new { description = "laptop" });

        var failed = await _admin.PostAsync($"/api/admin/users/{stuckUser.Id}/sign-out-everywhere", null);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("fixture", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Core's own sessions went first and stay gone.
        var viaSession = _factory.CreateClient();
        viaSession.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viaSession.GetAsync("/api/users/me")).StatusCode);

        var fineUser = await _factory.As("fine", "fine@example.com").GetJson<UserDto>("/api/users/me");
        var fine = await _admin.PostJson<SignOutEverywhereResultDto>(
            $"/api/admin/users/{fineUser.Id}/sign-out-everywhere", new { });
        Assert.Equal(new RevokedCredentialsDto("fixture", 0), Assert.Single(fine.Extensions));
    }

    // -----------------------------------------------------------------------
    // Routing
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ExtensionRoutes_AreNamespaced_AndCannotShadowCore()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoard("Routing", "ROUT");

        // The fixture declares [Route("api/tasks")]. The convention rewrote it,
        // so the namespaced route answers...
        var response = await _admin.GetAsync("/api/ext/fixture/api/tasks/milestones");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ...and core's /api/boards/{id}/tasks still belongs to core, returning
        // core's shape rather than anything the extension declared.
        var coreTasks = await _admin.GetJson<List<TaskDto>>($"/api/boards/{board.Id}/tasks");
        Assert.Empty(coreTasks);
    }

    [SkippableFact]
    public async Task UnknownExtensionRoute_Is404_NotTheSpaFallback()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _admin.GetAsync("/api/ext/fixture/api/tasks/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Persistence
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ExtensionMigrations_LandInTheirOwnSchema_AndLeaveCoreAlone()
    {
        postgres.SkipUnlessDockerAvailable();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        Assert.True(await ScalarBoolAsync(connection,
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'ext_fixture')"));

        // The extension's table and its OWN history table both live there.
        Assert.True(await ScalarBoolAsync(connection,
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema = 'ext_fixture' AND table_name = 'Milestones')"));
        Assert.True(await ScalarBoolAsync(connection,
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema = 'ext_fixture' AND table_name = '__EFMigrationsHistory')"));

        // Core's history table is untouched and unshared.
        Assert.True(await ScalarBoolAsync(connection,
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory')"));
    }

    [SkippableFact]
    public async Task Registry_RecordsTheLoadedExtension()
    {
        postgres.SkipUnlessDockerAvailable();

        var admin = await _admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        var fixture = Assert.Single(admin.Extensions, e => e.ExtensionId == "fixture");

        Assert.Equal("Loaded", fixture.State);
        Assert.Equal("ext_fixture", fixture.SchemaName);
        Assert.Equal("1.0.0", fixture.Version);
        Assert.NotNull(fixture.LastLoadedAt);
        // Loaded this boot, so purge is refused — that gate is what makes
        // dropping the schema safe without any locking.
        Assert.False(fixture.CanPurge);
        Assert.Equal(
            ["task.update", "task.setStatus", "task.setSchedule", "task.setBoard", "task.setAssignees", "epic.setAssignees"],
            fixture.Hooks);
    }

    [SkippableFact]
    public async Task Registry_MarksTheLastPackageAbsent_AfterItIsRemoved()
    {
        postgres.SkipUnlessDockerAvailable();

        // Restart with no packages at all — the case that used to skip
        // reconcile and leave the row reading Loaded.
        _factory.Dispose();
        _factory = new SoloApiFactory(_connectionString);
        _admin = _factory.As("admin", "admin@example.com", "Admin");

        var admin = await _admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        var fixture = Assert.Single(admin.Extensions, e => e.ExtensionId == "fixture");
        Assert.Equal("Absent", fixture.State);
        Assert.True(fixture.CanPurge);
    }

    [SkippableFact]
    public async Task Purge_IsRefused_WhileTheExtensionIsStillLoaded()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _admin.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, "/api/extensions/fixture/data")
        {
            Content = JsonContent.Create(new { confirmExtensionId = "fixture" }, options: TestClients.Json),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [SkippableFact]
    public async Task Purge_IsRefused_WhenTheConfirmationDoesNotMatch()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _admin.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, "/api/extensions/fixture/data")
        {
            Content = JsonContent.Create(new { confirmExtensionId = "wrong" }, options: TestClients.Json),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Purge_IsSuperAdminOnly()
    {
        postgres.SkipUnlessDockerAvailable();

        var stranger = _factory.As("stranger", "stranger@example.com");
        var response = await stranger.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, "/api/extensions/fixture/data")
        {
            Content = JsonContent.Create(new { confirmExtensionId = "fixture" }, options: TestClients.Json),
        });

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // Hooks
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task BeforeHook_CanVeto_WithProblemDetailsNamingTheExtension()
    {
        postgres.SkipUnlessDockerAvailable();

        var task = await CreateTaskAsync("Vetoed", "VETO");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/tasks/{task.Id}")
        {
            Content = JsonContent.Create(
                new { name = "renamed", version = task.Version }, options: TestClients.Json),
        };
        request.Headers.Add("X-Fixture-Hook", "reject");
        var response = await _admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("fixture", problem!["extension"].ToString());

        // The veto actually stopped the write.
        var unchanged = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal("Vetoed", unchanged.Name);
    }

    [SkippableFact]
    public async Task BeforeHook_CanMutateTheBoundRequest()
    {
        postgres.SkipUnlessDockerAvailable();

        var task = await CreateTaskAsync("Original", "MUTA");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/tasks/{task.Id}")
        {
            Content = JsonContent.Create(
                new { name = "from-the-client", version = task.Version }, options: TestClients.Json),
        };
        request.Headers.Add("X-Fixture-Hook", "mutate");
        var response = await _admin.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var updated = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal("mutated-by-hook", updated.Name);
    }

    [SkippableFact]
    public async Task BeforeHook_ThatThrows_FailsClosed()
    {
        postgres.SkipUnlessDockerAvailable();

        var task = await CreateTaskAsync("Guarded", "GRD1");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/tasks/{task.Id}")
        {
            Content = JsonContent.Create(
                new { name = "renamed", version = task.Version }, options: TestClients.Json),
        };
        request.Headers.Add("X-Fixture-Hook", "throw");
        var response = await _admin.SendAsync(request);

        // A hook that declared intent to veto and then broke must not be
        // silently ignored: the request is refused, and the extension is named.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var unchanged = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal("Guarded", unchanged.Name);
    }

    [SkippableFact]
    public async Task BeforeHook_ThatHangs_FailsClosedOnTimeout()
    {
        postgres.SkipUnlessDockerAvailable();

        var task = await CreateTaskAsync("Slow", "SLOW");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/tasks/{task.Id}")
        {
            Content = JsonContent.Create(
                new { name = "renamed", version = task.Version }, options: TestClients.Json),
        };
        request.Headers.Add("X-Fixture-Hook", "hang");
        var response = await _admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [SkippableFact]
    public async Task SetSchedule_IsHookable_AsTaskSetSchedule()
    {
        postgres.SkipUnlessDockerAvailable();

        var task = await CreateTaskAsync("Scheduled", "SCHD");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/tasks/{task.Id}/schedule")
        {
            Content = JsonContent.Create(
                new { deadlineAt = new DateOnly(2026, 10, 20), version = task.Version }, options: TestClients.Json),
        };
        request.Headers.Add("X-Fixture-Hook", "reject");
        var response = await _admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("fixture", problem!["extension"].ToString());
        Assert.Null((await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).DeadlineAt);
    }

    [SkippableFact]
    public async Task ActionsWithoutSoloOperation_AreNotHookable()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Unhooked", alias = "UNHK" });

        // Board creation carries no [SoloOperation], so the fixture's "reject"
        // header is inert on it.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/projects/{project.Id}/boards")
        {
            Content = JsonContent.Create(new { name = "Main", alias = "MAIN" }, options: TestClients.Json),
        };
        request.Headers.Add("X-Fixture-Hook", "reject");
        var response = await _admin.SendAsync(request);

        response.EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task BoardMoves_AndAssigneeChanges_AreHookable()
    {
        postgres.SkipUnlessDockerAvailable();

        var (project, board) = await CreateProjectWithBoard("Hooked", "HOOK");
        var other = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Other", alias = "OTHER" });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Stays put" });
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Epic" });
        var me = await _admin.GetJson<UserDto>("/api/users/me");

        // The three write paths contract 1.8 opened, each vetoed by the fixture.
        await AssertVetoedAsync(HttpMethod.Put, $"/api/tasks/{task.Id}/board", new { boardId = other.Id });
        await AssertVetoedAsync(HttpMethod.Put, $"/api/tasks/{task.Id}/assignees", new { userIds = new[] { me.Id } });
        await AssertVetoedAsync(HttpMethod.Put, $"/api/epics/{epic.Id}/assignees", new { userIds = new[] { me.Id } });

        var unchanged = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal(board.Id, unchanged.BoardId);
        Assert.Empty(unchanged.AssigneeIds);
    }

    // -----------------------------------------------------------------------
    // Member and comment reads (contract 1.8)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ExtensionQuery_ReadsMembersAndComments_OnlyWhereTheCallerCan()
    {
        postgres.SkipUnlessDockerAvailable();

        var (project, board) = await CreateProjectWithBoard("Readable", "READ");
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "pending@example.com", role = "Read" });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Talked about" });
        var comment = await _admin.PostJson<CommentDto>($"/api/tasks/{task.Id}/comments", new { body = "First!" });

        // What core's members endpoint shows a reader, pending invites included.
        var members = await _admin.GetJson<List<string>>($"/api/ext/fixture/api/tasks/members/{project.Id}");
        Assert.Equal(["admin@example.com", "pending@example.com"], members.Order());
        Assert.Equal("First!", (await _admin.GetJson<CommentDto>($"/api/ext/fixture/api/tasks/comments/{comment.Id}")).Body);
        Assert.Equal(["First!"], await _admin.GetJson<List<string>>($"/api/ext/fixture/api/tasks/task-comments/{task.Id}"));

        // Someone outside the project asks the same questions and learns nothing.
        var stranger = _factory.As("stranger", "stranger@example.com");
        Assert.Empty(await stranger.GetJson<List<string>>($"/api/ext/fixture/api/tasks/members/{project.Id}"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/ext/fixture/api/tasks/comments/{comment.Id}")).StatusCode);
        Assert.Empty(await stranger.GetJson<List<string>>($"/api/ext/fixture/api/tasks/task-comments/{task.Id}"));
    }

    // -----------------------------------------------------------------------
    // Background work (contract 1.8)
    // -----------------------------------------------------------------------

    private ISoloBackground Background => _factory.Services.GetRequiredKeyedService<ISoloBackground>("fixture");

    private async Task<Guid> GrantAsync()
    {
        var me = await _admin.GetJson<UserDto>("/api/users/me");
        (await _admin.PostAsync("/api/ext/fixture/api/tasks/background-grant", null)).EnsureSuccessStatusCode();
        return me.Id;
    }

    [SkippableFact]
    public async Task Background_RunsOnlyAsAUserWhoGrantedIt()
    {
        postgres.SkipUnlessDockerAvailable();

        var me = await _admin.GetJson<UserDto>("/api/users/me");
        var ran = false;
        Assert.False(await Background.RunAsAsync(me.Id, (_, _) => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);

        await GrantAsync();
        Assert.True(await Background.IsGrantedAsync(me.Id));
        Assert.True(await Background.RunAsAsync(me.Id, (_, _) => { ran = true; return Task.CompletedTask; }));
        Assert.True(ran);

        Assert.True(await Background.RevokeAsync(me.Id));
        Assert.False(await Background.RunAsAsync(me.Id, (_, _) => Task.CompletedTask));
    }

    [SkippableFact]
    public async Task Background_ReadsAndWritesAsTheUser_ThroughTheWholePipeline()
    {
        postgres.SkipUnlessDockerAvailable();

        var task = await CreateTaskAsync("Nightly", "NITE");
        var me = await GrantAsync();
        // The admin is a super admin; what follows shows background work isn't.
        (await _admin.GetAsync("/api/admin/users")).EnsureSuccessStatusCode();

        var ran = await Background.RunAsAsync(me, async (scope, ct) =>
        {
            Assert.Equal(me, scope.Caller.UserId);
            Assert.False(scope.Caller.IsSuperAdmin);
            Assert.NotEmpty(await scope.Query.ProjectsAsync(ct));

            // A write meets every extension's hooks, as one from a browser does...
            using var vetoed = new HttpRequestMessage(HttpMethod.Put, $"api/tasks/{task.Id}/status")
            {
                Content = JsonContent.Create(new { status = "InProgress" }, options: TestClients.Json),
            };
            vetoed.Headers.Add("X-Fixture-Hook", "reject");
            Assert.Equal(HttpStatusCode.Conflict, (await scope.Api.SendAsync(vetoed, ct)).StatusCode);

            // ...and lands when nothing objects.
            (await scope.Api.PutAsJsonAsync($"api/tasks/{task.Id}/status", new { status = "InProgress" },
                TestClients.Json, ct)).EnsureSuccessStatusCode();

            // Never super admin, whoever granted it.
            Assert.Equal(HttpStatusCode.Forbidden, (await scope.Api.GetAsync("api/admin/users", ct)).StatusCode);

            // And no way to mint a credential that would outlive the grant.
            var session = await scope.Api.PostAsJsonAsync("api/auth/sessions", new { description = "escape" }, ct);
            Assert.False(session.IsSuccessStatusCode);
        });

        Assert.True(ran);
        Assert.Equal(TaskState.InProgress, (await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).Status);
    }

    [SkippableFact]
    public async Task Background_GrantRevokedMidRun_StopsTheNextCall()
    {
        postgres.SkipUnlessDockerAvailable();

        var task = await CreateTaskAsync("Revoked", "RVKD");
        var me = await GrantAsync();

        await Background.RunAsAsync(me, async (scope, ct) =>
        {
            (await scope.Api.GetAsync($"api/tasks/{task.Id}", ct)).EnsureSuccessStatusCode();
            await Background.RevokeAsync(me, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, (await scope.Api.GetAsync($"api/tasks/{task.Id}", ct)).StatusCode);
        });
    }

    [SkippableFact]
    public async Task Background_GrantComesFromThePerson_NotFromBackgroundWork()
    {
        postgres.SkipUnlessDockerAvailable();

        // Outside any request there is no one to grant.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Background.GrantAsync());

        var me = await GrantAsync();
        await Background.RunAsAsync(me, async (scope, ct) =>
        {
            var response = await scope.Api.PostAsync("api/ext/fixture/api/tasks/background-grant", null, ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        });
    }

    [SkippableFact]
    public async Task SigningOutEverywhere_EndsBackgroundAccess()
    {
        postgres.SkipUnlessDockerAvailable();

        var me = await GrantAsync();

        var result = await _admin.PostJson<SignOutEverywhereResultDto>(
            $"/api/admin/users/{me}/sign-out-everywhere", new { });

        // Reported against the extension, as its own credentials would be.
        Assert.Contains(result.Extensions, r => r is { ExtensionId: "fixture", Count: 1 });
        Assert.False(await Background.IsGrantedAsync(me));
        Assert.False(await Background.RunAsAsync(me, (_, _) => Task.CompletedTask));
    }

    // -----------------------------------------------------------------------
    // The disabled gate
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task DisabledExtension_Answers503_NamingTheReason()
    {
        postgres.SkipUnlessDockerAvailable();

        // Simulates what a failed migration does at startup: the routes are
        // already mapped by then, so the gate has to refuse at request time.
        var state = _factory.Services.GetRequiredService<ExtensionState>();
        state.Disable("fixture", "Its database migration failed: relation does not exist.");

        try
        {
            var response = await _admin.GetAsync("/api/ext/fixture/api/tasks/milestones");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("migration failed", body);

            // It also stops being advertised to the client, so no nav entry can
            // point at a route its assembly will not serve.
            var advertised = await _admin.GetJson<List<ClientExtensionDto>>("/api/extensions");
            Assert.DoesNotContain(advertised, e => e.Id == "fixture");
        }
        finally
        {
            // The state singleton is shared by every test in this class.
            state.Enable("fixture");
        }
    }

    // -----------------------------------------------------------------------
    // Version reporting (the client compat check's prerequisite)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Version_IsReportedAnonymously()
    {
        postgres.SkipUnlessDockerAvailable();

        var anonymous = _factory.CreateClient();
        var version = await anonymous.GetJson<InstanceVersionDto>("/api/config/version");

        // The endpoint's whole job is to report this build's contract, so that
        // is what it is checked against — a literal here would only have to be
        // edited on every bump without catching anything.
        Assert.Equal(ContractVersion.Current, version.ExtensionApiVersion);
        Assert.False(string.IsNullOrWhiteSpace(version.ServerVersion));
    }

    // -----------------------------------------------------------------------

    private async Task<(ProjectDto Project, BoardDto Board)> CreateProjectWithBoard(string name, string alias)
    {
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name, alias });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Main", alias = "MAIN" });
        return (project, board);
    }

    private async Task<TaskDto> CreateTaskAsync(string taskName, string alias)
    {
        var (_, board) = await CreateProjectWithBoard(taskName, alias);
        return await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = taskName });
    }

    private async Task AssertVetoedAsync(HttpMethod method, string url, object body)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body, options: TestClients.Json),
        };
        request.Headers.Add("X-Fixture-Hook", "reject");
        var response = await _admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("fixture", problem!["extension"].ToString());
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
