using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solo.Data;
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
        Assert.Equal(["task.update", "task.setStatus", "task.setSchedule"], fixture.Hooks);
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

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
