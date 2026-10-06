using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The Roadmaps extension end to end: its package loads and migrates its own
/// schema, its endpoints authorize through core's project policies, and it
/// reads core only through ISoloQuery.
/// </summary>
/// <remarks>
/// Like <see cref="McpExtensionTests"/>, the package is discovered on disk and
/// loaded into its own AssemblyLoadContext — this project doesn't reference it —
/// so the shapes are read as JSON rather than through the extension's types.
/// </remarks>
[Collection("postgres")]
public sealed class RoadmapExtensionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Base = "/api/ext/roadmap";

    private string _connectionString = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync("roadmap");
        _factory = new SoloApiFactory(_connectionString, TestExtensions.Roadmap);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // The package (SOLO-FEAT-11)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task The_package_loads_and_migrates_its_own_schema()
    {
        postgres.SkipUnlessDockerAvailable();

        var admin = await _admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        var roadmap = Assert.Single(admin.Extensions, e => e.ExtensionId == "roadmap");
        Assert.Equal("Loaded", roadmap.State);
        Assert.Equal("ext_roadmap", roadmap.SchemaName);
        Assert.Empty(roadmap.Hooks);

        var client = Assert.Single(await _admin.GetJson<List<ClientExtensionDto>>("/api/extensions"),
            e => e.Id == "roadmap");
        Assert.Equal("Solo.Roadmap.Client", client.ClientAssembly);
        Assert.Empty(client.Nav);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var tables = await StringsAsync(connection,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'ext_roadmap' ORDER BY 1");
        Assert.Equal(["Entries", "Roadmaps", "Settings", "__EFMigrationsHistory"], tables);
    }

    /// <summary>The page's stylesheet and module load as ordinary same-origin subresources.</summary>
    [SkippableFact]
    public async Task Its_content_is_served_to_the_browser()
    {
        postgres.SkipUnlessDockerAvailable();

        var anonymous = _factory.CreateClient();
        var script = await anonymous.GetAsync("/api/extensions/roadmap/content/roadmap.js");
        var styles = await anonymous.GetAsync("/api/extensions/roadmap/content/roadmap.css");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, styles.StatusCode);
        Assert.Equal("text/css", styles.Content.Headers.ContentType!.MediaType);
    }

    // -----------------------------------------------------------------------
    // Data (SOLO-FEAT-12, through the running host)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Deleting_a_task_takes_it_off_every_roadmap()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Deleted", "DELT");
        var task = await TaskAsync(board, "Doomed");
        var first = await RoadmapAsync(project, "First");
        var second = await RoadmapAsync(project, "Second");
        await AddAsync(project, first, task);
        await AddAsync(project, second, task);
        Assert.Equal(2, await EntryCountAsync(task.Id));

        (await _admin.DeleteAsync($"/api/tasks/{task.Id}")).EnsureSuccessStatusCode();

        // Straight from the table, not through the timeline — the read path
        // prunes too, and this is about the event handler.
        Assert.Equal(0, await EntryCountAsync(task.Id));
    }

    /// <summary>A move raises a delete on the board it leaves; the task is still in the project.</summary>
    [SkippableFact]
    public async Task Moving_a_task_to_another_board_keeps_its_entries()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Moved", "MOVE");
        var other = await _admin.PostJson<BoardDto>($"/api/projects/{project.Id}/boards",
            new { name = "Other", alias = "OTHER" });
        var task = await TaskAsync(board, "Travelling");
        var roadmap = await RoadmapAsync(project, "Plan");
        await AddAsync(project, roadmap, task);

        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/board", new { boardId = other.Id });

        Assert.Equal(1, await EntryCountAsync(task.Id));
        var entry = Assert.Single(await EntriesAsync(project, roadmap));
        Assert.Equal("OTHER", entry["boardAlias"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task The_settings_row_is_created_on_first_read_with_the_defaults()
    {
        postgres.SkipUnlessDockerAvailable();

        var settings = await _admin.GetJson<JsonObject>($"{Base}/settings");

        Assert.Equal(365, settings["pastDays"]!.GetValue<int>());
        Assert.Equal(730, settings["futureDays"]!.GetValue<int>());
        Assert.Equal("Monday", settings["sprintStartDay"]!.GetValue<string>());
        Assert.Equal(2, settings["sprintLengthWeeks"]!.GetValue<int>());

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        Assert.Equal(["1"], await StringsAsync(connection, "SELECT \"Id\"::text FROM ext_roadmap.\"Settings\""));
    }

    // -----------------------------------------------------------------------
    // Authorization (SOLO-FEAT-13). First on purpose: a roadmap readable
    // through the wrong project is the failure that matters most.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task A_non_member_can_neither_list_nor_read_a_projects_roadmaps()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, _) = await ProjectAsync("Private", "PRIV");
        var roadmap = await RoadmapAsync(project, "Secret plan");
        var stranger = _factory.As("stranger", "stranger@example.com");

        Assert.True((await stranger.GetAsync($"{Base}/projects/{project.Id}/roadmaps")).StatusCode
            is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.True((await stranger.GetAsync($"{Base}/projects/{project.Id}/roadmaps/{roadmap}")).StatusCode
            is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.True((await stranger.PostAsJsonAsync($"{Base}/projects/{project.Id}/roadmaps",
            new { title = "Mine now" }, TestClients.Json)).StatusCode
            is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task A_read_only_member_can_read_but_not_write()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("ReadOnly", "RDONLY");
        var task = await TaskAsync(board, "Visible");
        var roadmap = await RoadmapAsync(project, "Plan");
        var entry = await AddAsync(project, roadmap, task);
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members",
            new { email = "reader@example.com", role = "Read" });
        var reader = _factory.As("reader", "reader@example.com");

        Assert.Single(await reader.GetJson<List<JsonObject>>($"{Base}/projects/{project.Id}/roadmaps"));
        Assert.Single(await EntriesAsync(project, roadmap, reader));
        Assert.Single(await reader.GetJson<List<JsonObject>>($"{Base}/tasks/{task.Id}/roadmaps"));

        var url = $"{Base}/projects/{project.Id}/roadmaps";
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync(url, new { title = "No" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PutAsJsonAsync($"{url}/{roadmap}", new { title = "No", version = 0 }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{url}/{roadmap}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{url}/{roadmap}/entries",
            new { taskId = task.Id, anchorDay = "2026-10-01" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{url}/{roadmap}/entries/{entry}/rank",
            new { }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{url}/{roadmap}/entries/{entry}/anchor",
            new { anchorDay = "2026-10-09" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.DeleteAsync($"{url}/{roadmap}/entries/{entry}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{url}/{roadmap}/entries/remove",
            new { entryIds = new[] { entry } }, TestClients.Json)).StatusCode);
        Assert.Single(await EntriesAsync(project, roadmap, reader));
    }

    [SkippableFact]
    public async Task A_roadmap_id_under_another_projects_route_is_404()
    {
        postgres.SkipUnlessDockerAvailable();
        var (home, board) = await ProjectAsync("Home", "HOME");
        var (away, _) = await ProjectAsync("Away", "AWAY");
        var task = await TaskAsync(board, "Local");
        var roadmap = await RoadmapAsync(home, "Plan");
        var entry = await AddAsync(home, roadmap, task);

        // The caller may write to both projects; the pairing is still wrong.
        var wrong = $"{Base}/projects/{away.Id}/roadmaps/{roadmap}";
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.PutAsJsonAsync(wrong, new { title = "Hijacked", version = 0 }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsJsonAsync($"{wrong}/entries",
            new { taskId = task.Id, anchorDay = "2026-10-01" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.PutAsJsonAsync($"{wrong}/entries/{entry}/rank", new { }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsJsonAsync($"{wrong}/entries/{entry}/anchor",
            new { anchorDay = "2026-10-09" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"{wrong}/entries/{entry}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsJsonAsync($"{wrong}/entries/remove",
            new { entryIds = new[] { entry } }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync(wrong)).StatusCode);

        Assert.Empty(await _admin.GetJson<List<JsonObject>>($"{Base}/projects/{away.Id}/roadmaps"));
        Assert.Single(await EntriesAsync(home, roadmap));
    }

    // -----------------------------------------------------------------------
    // Validation and behavior (SOLO-FEAT-13)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Adding_a_task_from_another_project_is_400()
    {
        postgres.SkipUnlessDockerAvailable();
        var (home, _) = await ProjectAsync("Mine", "MINE");
        var (_, otherBoard) = await ProjectAsync("Theirs", "THEIRS");
        var foreign = await TaskAsync(otherBoard, "Not yours");
        var roadmap = await RoadmapAsync(home, "Plan");

        var response = await _admin.PostAsJsonAsync($"{Base}/projects/{home.Id}/roadmaps/{roadmap}/entries",
            new { taskId = foreign.Id, anchorDay = "2026-10-01" }, TestClients.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Adding_the_same_task_twice_is_409()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Twice", "TWICE");
        var task = await TaskAsync(board, "Once only");
        var roadmap = await RoadmapAsync(project, "Plan");
        await AddAsync(project, roadmap, task);

        var again = await _admin.PostAsJsonAsync($"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries",
            new { taskId = task.Id, anchorDay = "2026-10-02" }, TestClients.Json);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [SkippableFact]
    public async Task Entries_come_back_with_their_task_board_and_anchor_day()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Payload", "PAYLOAD");
        var task = await TaskAsync(board, "Drawn");
        var roadmap = await RoadmapAsync(project, "Plan");
        await AddAsync(project, roadmap, task, "2026-10-07");

        var timeline = await _admin.GetJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps/{roadmap}");

        Assert.Equal("Plan", timeline["title"]!.GetValue<string>());
        Assert.Equal("PAYLOAD", timeline["projectAlias"]!.GetValue<string>());
        Assert.Equal(365, timeline["settings"]!["pastDays"]!.GetValue<int>());
        var entry = Assert.Single(timeline["entries"]!.AsArray())!;
        Assert.Equal("2026-10-07", entry["anchorDay"]!.GetValue<string>());
        Assert.Equal("PAYLOAD", entry["boardAlias"]!.GetValue<string>());
        Assert.Equal(task.Id, entry["task"]!["id"]!.GetValue<Guid>());
        Assert.Equal(task.Number, entry["task"]!["number"]!.GetValue<int>());
    }

    /// <summary>
    /// A selection's Remove: the entries named go in one request, and one
    /// already gone, or on another roadmap, is passed over rather than failing
    /// the rest.
    /// </summary>
    [SkippableFact]
    public async Task Removing_several_entries_takes_them_off_together_and_nothing_else()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Bulk", "BULK");
        var first = await TaskAsync(board, "First");
        var second = await TaskAsync(board, "Second");
        var third = await TaskAsync(board, "Third");
        var roadmap = await RoadmapAsync(project, "Plan");
        var other = await RoadmapAsync(project, "Other");
        var firstEntry = await AddAsync(project, roadmap, first);
        await AddAsync(project, roadmap, second);
        var thirdEntry = await AddAsync(project, roadmap, third);
        var elsewhere = await AddAsync(project, other, first);
        var url = $"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries/remove";

        var removed = await _admin.PostAsJsonAsync(url,
            new { entryIds = new[] { firstEntry, thirdEntry, Guid.NewGuid(), elsewhere } }, TestClients.Json);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(["Second"], await OrderAsync(project, roadmap));
        Assert.Single(await EntriesAsync(project, other));
        Assert.Equal(3, (await _admin.GetJson<List<JsonObject>>($"/api/boards/{board.Id}/tasks")).Count);

        // Already gone: nothing left to do is still done.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.PostAsJsonAsync(url, new { entryIds = new[] { firstEntry } }, TestClients.Json)).StatusCode);
    }

    [SkippableFact]
    public async Task A_remove_naming_no_entries_or_too_many_is_400()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, _) = await ProjectAsync("Bounds", "BOUNDS");
        var roadmap = await RoadmapAsync(project, "Plan");
        var url = $"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries/remove";
        // One over RoadmapLimits.MaxRemoveAtOnce: the package is laid out for
        // the host to load, not referenced, so its constants aren't in reach.
        var tooMany = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync(url, new { entryIds = Array.Empty<Guid>() }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync(url, new { entryIds = tooMany }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync(url, new { }, TestClients.Json)).StatusCode);
    }

    /// <summary>A pill dragged along the timeline moves its own day; the task still has no dates.</summary>
    [SkippableFact]
    public async Task Moving_a_pill_moves_its_anchor_day_and_leaves_the_task_alone()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Pill", "PILL");
        var task = await TaskAsync(board, "Undated");
        var roadmap = await RoadmapAsync(project, "Plan");
        var entry = await AddAsync(project, roadmap, task, "2026-10-07");
        var url = $"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries/{entry}/anchor";

        var moved = await _admin.PutAsJsonAsync(url, new { anchorDay = "2026-10-12" }, TestClients.Json);

        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        var drawn = Assert.Single(await EntriesAsync(project, roadmap));
        Assert.Equal("2026-10-12", drawn["anchorDay"]!.GetValue<string>());
        var untouched = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Null(untouched.DeadlineAt);
        Assert.Null(untouched.StartAt);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PutAsJsonAsync(url, new { }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsJsonAsync(
            $"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries/{Guid.NewGuid()}/anchor",
            new { anchorDay = "2026-10-12" }, TestClients.Json)).StatusCode);
    }

    [SkippableFact]
    public async Task Reranking_keeps_the_new_order()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Order", "ORDER");
        var roadmap = await RoadmapAsync(project, "Plan");
        var a = await TaskAsync(board, "A");
        var b = await TaskAsync(board, "B");
        var c = await TaskAsync(board, "C");
        await AddAsync(project, roadmap, a);
        var entryB = await AddAsync(project, roadmap, b);
        var entryC = await AddAsync(project, roadmap, c);

        var rank = $"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries";
        (await _admin.PutAsJsonAsync($"{rank}/{entryC}/rank", new { }, TestClients.Json)).EnsureSuccessStatusCode();
        Assert.Equal(["C", "A", "B"], await OrderAsync(project, roadmap));

        (await _admin.PutAsJsonAsync($"{rank}/{entryB}/rank", new { followingId = entryC }, TestClients.Json))
            .EnsureSuccessStatusCode();
        Assert.Equal(["B", "C", "A"], await OrderAsync(project, roadmap));

        // The board's own order is untouched.
        var onBoard = await _admin.GetJson<List<TaskDto>>($"/api/boards/{board.Id}/tasks");
        Assert.Equal(["A", "B", "C"], onBoard.Select(t => t.Name));

        var unknown = await _admin.PutAsJsonAsync($"{rank}/{entryB}/rank",
            new { precedingId = Guid.NewGuid() }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [SkippableFact]
    public async Task A_task_deleted_behind_the_extensions_back_drops_out_of_the_payload()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Pruned", "PRUNED");
        var kept = await TaskAsync(board, "Kept");
        var lost = await TaskAsync(board, "Lost");
        var roadmap = await RoadmapAsync(project, "Plan");
        await AddAsync(project, roadmap, kept);
        await AddAsync(project, roadmap, lost);

        // Straight out of core's table, so no event reaches the extension —
        // the missed-event case the read path exists for.
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM \"Tasks\" WHERE \"Id\" = '{lost.Id}'";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(["Kept"], await OrderAsync(project, roadmap));
        Assert.Equal(0, await EntryCountAsync(lost.Id));
    }

    [SkippableFact]
    public async Task A_rename_with_a_stale_version_is_409()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, _) = await ProjectAsync("Rename", "RENAME");
        var created = await _admin.PostJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps", new { title = "Q3" });
        var id = created["id"]!.GetValue<Guid>();
        var version = created["version"]!.GetValue<uint>();
        var url = $"{Base}/projects/{project.Id}/roadmaps/{id}";

        var renamed = await _admin.PutJson<JsonObject>(url, new { title = "Q4", version });
        Assert.Equal("Q4", renamed["title"]!.GetValue<string>());

        var stale = await _admin.PutAsJsonAsync(url, new { title = "Q1", version }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Q4", (await _admin.GetJson<JsonObject>(url))["title"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task The_list_is_ordered_by_title_and_delete_takes_the_entries_with_it()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Listed", "LISTED");
        var task = await TaskAsync(board, "On it");
        var zulu = await RoadmapAsync(project, "Zulu");
        await RoadmapAsync(project, "Alpha");
        await AddAsync(project, zulu, task);

        var list = await _admin.GetJson<List<JsonObject>>($"{Base}/projects/{project.Id}/roadmaps");
        Assert.Equal(["Alpha", "Zulu"], list.Select(r => r["title"]!.GetValue<string>()));

        (await _admin.DeleteAsync($"{Base}/projects/{project.Id}/roadmaps/{zulu}")).EnsureSuccessStatusCode();

        Assert.Equal(0, await EntryCountAsync(task.Id));
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/tasks/{task.Id}")).StatusCode);
    }

    [SkippableFact]
    public async Task A_tasks_roadmaps_are_listed_for_its_task_view()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Chips", "CHIPS");
        var task = await TaskAsync(board, "Shared");
        var loner = await TaskAsync(board, "Alone");
        var launch = await RoadmapAsync(project, "Launch");
        var polish = await RoadmapAsync(project, "Polish");
        await AddAsync(project, polish, task);
        await AddAsync(project, launch, task);

        var roadmaps = await _admin.GetJson<List<JsonObject>>($"{Base}/tasks/{task.Id}/roadmaps");

        Assert.Equal([launch, polish], roadmaps.Select(r => r["id"]!.GetValue<Guid>()));
        Assert.All(roadmaps, r => Assert.Equal("CHIPS", r["projectAlias"]!.GetValue<string>()));
        Assert.Empty(await _admin.GetJson<List<JsonObject>>($"{Base}/tasks/{loner.Id}/roadmaps"));
    }

    // -----------------------------------------------------------------------
    // Settings (SOLO-FEAT-14)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Only_a_super_admin_changes_the_settings()
    {
        postgres.SkipUnlessDockerAvailable();
        var member = _factory.As("member", "member@example.com");
        // Any member can read them: the timeline needs them.
        var (project, _) = await ProjectAsync("Settings", "SETTINGS");
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members",
            new { email = "member@example.com", role = "ReadWrite" });
        var settings = await member.GetJson<JsonObject>($"{Base}/settings");

        var response = await member.PutAsJsonAsync($"{Base}/settings", Settings(settings, pastDays: 30), TestClients.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData(0, 730, "Monday", 2)]
    [InlineData(3651, 730, "Monday", 2)]
    [InlineData(365, 0, "Monday", 2)]
    [InlineData(365, 3651, "Monday", 2)]
    [InlineData(365, 730, "Monday", 0)]
    [InlineData(365, 730, "Monday", 53)]
    [InlineData(365, 730, 7, 2)]          // no eighth day of the week
    [InlineData(365, 730, "Someday", 2)]
    public async Task Out_of_range_settings_are_400(int pastDays, int futureDays, object startDay, int weeks)
    {
        postgres.SkipUnlessDockerAvailable();
        var current = await _admin.GetJson<JsonObject>($"{Base}/settings");

        var response = await _admin.PutAsJsonAsync($"{Base}/settings", new
        {
            pastDays,
            futureDays,
            sprintStartDay = startDay,
            sprintLengthWeeks = weeks,
            version = current["version"]!.GetValue<uint>(),
        }, TestClients.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Saved_sprint_settings_are_read_back_and_reach_the_timeline()
    {
        postgres.SkipUnlessDockerAvailable();
        var current = await _admin.GetJson<JsonObject>($"{Base}/settings");

        await _admin.PutJson<JsonObject>($"{Base}/settings",
            Settings(current, pastDays: 30, futureDays: 90, startDay: "Wednesday", weeks: 3));

        var reread = await _admin.GetJson<JsonObject>($"{Base}/settings");
        Assert.Equal("Wednesday", reread["sprintStartDay"]!.GetValue<string>());
        Assert.Equal(3, reread["sprintLengthWeeks"]!.GetValue<int>());
        Assert.Equal((30, 90), (reread["pastDays"]!.GetValue<int>(), reread["futureDays"]!.GetValue<int>()));

        var (project, _) = await ProjectAsync("Carried", "CARRIED");
        var roadmap = await RoadmapAsync(project, "Plan");
        var timeline = await _admin.GetJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps/{roadmap}");
        Assert.Equal("Wednesday", timeline["settings"]!["sprintStartDay"]!.GetValue<string>());
        Assert.Equal(3, timeline["settings"]!["sprintLengthWeeks"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task Settings_saved_over_a_stale_version_are_409()
    {
        postgres.SkipUnlessDockerAvailable();
        var current = await _admin.GetJson<JsonObject>($"{Base}/settings");
        await _admin.PutJson<JsonObject>($"{Base}/settings", Settings(current, pastDays: 100));

        var stale = await _admin.PutAsJsonAsync($"{Base}/settings", Settings(current, pastDays: 200), TestClients.Json);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Live updates (Roadmaps 1.2)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Changes_are_announced_to_open_pages_but_not_echoed_to_whoever_made_them()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await ProjectAsync("Live", "LIVE");
        var task = await TaskAsync(board, "Watched");
        var roadmap = await RoadmapAsync(project, "Plan");
        var topic = $"roadmap:{roadmap:N}";

        await using var page = Connect();
        var heard = new List<TopicMessage>();
        var arrived = new SemaphoreSlim(0);
        page.On<TopicMessage>(nameof(IBoardClient.TopicMessage), m => { lock (heard) { heard.Add(m); } arrived.Release(); });
        await page.StartAsync();
        await page.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, topic);
        await page.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, "roadmap:list");

        var read = 0;
        async Task<TopicMessage> NextAsync()
        {
            Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(10)), "no message arrived");
            lock (heard) { return heard[read++]; }
        }

        // Someone else adds a task: the page hears its entries changed.
        await AddAsync(project, roadmap, task);
        var added = await NextAsync();
        Assert.Equal((topic, "entries"), (added.Topic, added.Kind));

        // A core edit to a task on the roadmap reaches the roadmap's page, naming the task.
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/schedule",
            new { startAt = "2026-10-05", deadlineAt = "2026-10-09", version = task.Version });
        var taskMessage = await NextAsync();
        Assert.Equal((topic, "task", task.Id.ToString()), (taskMessage.Topic, taskMessage.Kind, taskMessage.Data));

        // A rename tells lists and the page.
        var current = await _admin.GetJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps/{roadmap}");
        await _admin.PutJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps/{roadmap}",
            new { title = "Renamed", version = current["version"]!.GetValue<uint>() });
        var renamed = new[] { await NextAsync(), await NextAsync() }.Select(m => (m.Topic, m.Kind)).ToList();
        Assert.Contains(("roadmap:list", "changed"), renamed);
        Assert.Contains((topic, "renamed"), renamed);

        // Several taken off at once are one change, not one per entry.
        var entry = (await EntriesAsync(project, roadmap)).Single()["id"]!.GetValue<Guid>();
        await _admin.PostAsJsonAsync($"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries/remove",
            new { entryIds = new[] { entry } }, TestClients.Json);
        var removed = await NextAsync();
        Assert.Equal((topic, "entries"), (removed.Topic, removed.Kind));

        // The page's own change, sent with its connection id, isn't echoed back to it.
        var other = await TaskAsync(board, "Mine");
        var mine = new HttpRequestMessage(HttpMethod.Post, $"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries")
        {
            Content = JsonContent.Create(new { taskId = other.Id, anchorDay = "2026-10-01" }, options: TestClients.Json),
        };
        mine.Headers.Add(BoardHubConstants.ConnectionIdHeader, page.ConnectionId);
        (await _admin.SendAsync(mine)).EnsureSuccessStatusCode();
        Assert.False(await arrived.WaitAsync(TimeSpan.FromMilliseconds(800)), "the change echoed back to its sender");
    }

    private HubConnection Connect() =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubConstants.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.SubHeader] = "admin";
                options.Headers[TestAuthHandler.EmailHeader] = "admin@example.com";
                options.Headers[TestAuthHandler.NameHeader] = "Admin";
            })
            .Build();

    // -----------------------------------------------------------------------

    private static object Settings(JsonObject current, int? pastDays = null, int? futureDays = null,
        string? startDay = null, int? weeks = null) => new
    {
        pastDays = pastDays ?? current["pastDays"]!.GetValue<int>(),
        futureDays = futureDays ?? current["futureDays"]!.GetValue<int>(),
        sprintStartDay = startDay ?? current["sprintStartDay"]!.GetValue<string>(),
        sprintLengthWeeks = weeks ?? current["sprintLengthWeeks"]!.GetValue<int>(),
        version = current["version"]!.GetValue<uint>(),
    };

    private async Task<(ProjectDto Project, BoardDto Board)> ProjectAsync(string name, string alias)
    {
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name, alias });
        var board = await _admin.PostJson<BoardDto>($"/api/projects/{project.Id}/boards", new { name, alias });
        return (project, board);
    }

    private Task<TaskDto> TaskAsync(BoardDto board, string name) =>
        _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name });

    private async Task<Guid> RoadmapAsync(ProjectDto project, string title) =>
        (await _admin.PostJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps", new { title }))["id"]!
        .GetValue<Guid>();

    private async Task<Guid> AddAsync(ProjectDto project, Guid roadmap, TaskDto task, string day = "2026-10-01") =>
        (await _admin.PostJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps/{roadmap}/entries",
            new { taskId = task.Id, anchorDay = day }))["id"]!.GetValue<Guid>();

    private async Task<List<JsonNode>> EntriesAsync(ProjectDto project, Guid roadmap, HttpClient? client = null) =>
        (await (client ?? _admin).GetJson<JsonObject>($"{Base}/projects/{project.Id}/roadmaps/{roadmap}"))
        ["entries"]!.AsArray().Select(e => e!).ToList();

    private async Task<List<string>> OrderAsync(ProjectDto project, Guid roadmap) =>
        (await EntriesAsync(project, roadmap)).Select(e => e["task"]!["name"]!.GetValue<string>()).ToList();

    private async Task<long> EntryCountAsync(Guid taskId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM ext_roadmap.\"Entries\" WHERE \"TaskId\" = '{taskId}'";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> StringsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
