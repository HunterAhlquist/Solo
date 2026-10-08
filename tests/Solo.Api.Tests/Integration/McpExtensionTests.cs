using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Solo.Shared.Dtos;
using Npgsql;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The MCP extension end to end: its own credential, its own schema, and tool
/// calls that re-enter the API as the token's owner.
/// </summary>
/// <remarks>
/// The package is discovered on disk and loaded into its own
/// <c>AssemblyLoadContext</c> — the test project references it no more than a
/// deployment does. Everything asserted here therefore holds for the zip a
/// release actually ships.
/// </remarks>
[Collection("postgres")]
public sealed class McpExtensionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Endpoint = "/api/ext/mcp";
    private const string Tokens = "/api/ext/mcp/tokens";

    private string _connectionString = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync("mcp");
        _factory = new SoloApiFactory(_connectionString, TestExtensions.Mcp);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // The credential. Written first: an MCP endpoint reachable by anything
    // other than a token a user granted and an admin can revoke is the failure
    // that matters most here.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task A_token_value_is_returned_once_and_never_again()
    {
        postgres.SkipUnlessDockerAvailable();

        var minted = await MintTokenAsync("Claude Code");
        Assert.StartsWith("solomcp_", minted.Value);

        // Every later read is metadata only — the database holds a hash.
        var status = await _admin.GetJson<JsonObject>(Tokens);
        Assert.DoesNotContain(minted.Value, status.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(minted.Id, Guid.Parse(status["active"]!["id"]!.GetValue<string>()));
        Assert.Equal("Claude Code", status["active"]!["description"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Regenerating_blocks_the_previous_token()
    {
        postgres.SkipUnlessDockerAvailable();

        var first = await MintTokenAsync();
        Assert.Equal(HttpStatusCode.OK, (await ListToolsAsync(McpClient(first.Value))).Status);

        var second = await MintTokenAsync();
        Assert.NotEqual(first.Value, second.Value);

        // The old one is dead the moment the new one exists...
        Assert.Equal(HttpStatusCode.Unauthorized, (await ListToolsAsync(McpClient(first.Value))).Status);
        // ...and the new one works.
        Assert.Equal(HttpStatusCode.OK, (await ListToolsAsync(McpClient(second.Value))).Status);

        // The revoked row survives, which is what keeps the admin audit honest.
        var status = await _admin.GetJson<JsonObject>(Tokens);
        var history = status["history"]!.AsArray();
        Assert.Equal(2, history.Count);
        var revoked = history.Single(t => Guid.Parse(t!["id"]!.GetValue<string>()) == first.Id)!;
        Assert.Equal("Regenerated", revoked["revokedReason"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Revoking_leaves_the_account_with_no_live_token()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var response = await _admin.DeleteAsync($"{Tokens}/current");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ListToolsAsync(McpClient(token.Value))).Status);
        Assert.Null(((JsonNode?)await _admin.GetJson<JsonObject>(Tokens))!["active"]);
    }

    /// <summary>
    /// A credential must not be able to mint its replacement, or revocation
    /// would never be final.
    /// </summary>
    [SkippableFact]
    public async Task An_mcp_token_cannot_manage_tokens()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var client = McpClient(token.Value);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(Tokens, new { }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Tokens)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Tokens}/current")).StatusCode);
    }

    /// <summary>
    /// Nor any other credential. A token that could mint a session would
    /// outlive its own revocation, and a session can mint a token — so the two
    /// would renew each other forever.
    /// </summary>
    [SkippableFact]
    public async Task An_mcp_token_cannot_mint_a_session_or_complete_a_desktop_sign_in()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var client = McpClient(token.Value);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/auth/sessions", new { }, TestClients.Json)).StatusCode);

        var flow = await _factory.CreateClient().PostJson<DesktopAuthFlowDto>("/api/auth/desktop/start", new
        {
            description = "Solo Desktop (test)",
            codeChallenge = Auth.DesktopSignInFlows.PkceChallenge(new string('v', 43)),
            loopbackPort = 50123,
        });
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/auth/desktop/complete", new { state = flow.State }, TestClients.Json))
            .StatusCode);

        // Or manage the sessions its owner has.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/auth/sessions")).StatusCode);
    }

    /// <summary>
    /// Core ignores an email the provider marks unverified, so an address
    /// nobody proved can't activate an invite. A token must not launder one
    /// into an identity that looks verified.
    /// </summary>
    [SkippableFact]
    public async Task An_unverified_email_cannot_claim_an_invite_through_a_token()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Secret", alias = "SECRET" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "victim@example.com", role = "ReadWrite" });

        var impostor = _factory.As("impostor", "victim@example.com", "Impostor");
        impostor.DefaultRequestHeaders.Add(TestAuthHandler.EmailVerifiedHeader, "false");

        Assert.Equal(HttpStatusCode.Forbidden, (await impostor.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await impostor.PostAsJsonAsync(Tokens, new { description = "x" }, TestClients.Json)).StatusCode);

        var members = await _admin.GetJson<List<ProjectMemberDto>>($"/api/projects/{project.Id}/members");
        Assert.True(members.Single(m => m.Email == "victim@example.com").IsPending);
    }

    /// <summary>
    /// A token carries its owner's identity and nothing else. The email it was
    /// minted with is a snapshot: replayed later, provisioning would read it as
    /// the provider's word, overwrite the user's address and hand them any
    /// invite to the old one.
    /// </summary>
    [SkippableFact]
    public async Task A_token_activates_no_invite_to_the_address_it_was_minted_with()
    {
        postgres.SkipUnlessDockerAvailable();

        var home = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Home", alias = "HOME" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{home.Id}/members", new { email = "old@example.com", role = "Read" });
        var mover = _factory.As("mover", "old@example.com", "Mover");
        var token = await MintTokenAsync(client: mover);

        // The address changes at the provider, then someone invites the old one.
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """UPDATE "Users" SET "Email" = 'new@example.com' WHERE "Email" = 'old@example.com'""", connection);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var other = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Other", alias = "OTHER" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{other.Id}/members", new { email = "old@example.com", role = "ReadWrite" });

        var projects = await McpClient(token.Value).GetJson<List<ProjectSummaryDto>>("/api/projects");
        Assert.Contains(projects, p => p.Id == home.Id);
        Assert.DoesNotContain(projects, p => p.Id == other.Id);
    }

    /// <summary>
    /// The endpoint takes an MCP token specifically. A browser session reaching
    /// it would be MCP usage nobody could see in the audit or cut off.
    /// </summary>
    [SkippableFact]
    public async Task A_browser_session_cannot_drive_the_mcp_endpoint()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _admin.PostAsync(Endpoint, Rpc(1, "tools/list"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_unknown_token_is_refused()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await ListToolsAsync(McpClient("solomcp_not-a-real-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    }

    // -----------------------------------------------------------------------
    // Protocol
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Initialize_agrees_a_protocol_and_advertises_tools()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var result = await CallAsync(McpClient(token.Value), 1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1.0" },
            ["capabilities"] = new JsonObject(),
        });

        // The client's revision is echoed rather than forced up.
        Assert.Equal("2025-03-26", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("solo", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [SkippableFact]
    public async Task Tools_list_includes_epic_and_group_management()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var (status, body) = await ListToolsAsync(McpClient(token.Value));
        Assert.Equal(HttpStatusCode.OK, status);

        var names = body!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();

        Assert.Equal(
        [
            "create_epic", "create_epic_group", "create_subtask", "create_task", "delete_epic", "delete_epic_group",
            "get_me", "get_task", "get_task_llm_work", "get_workflow", "list_boards", "list_epic_groups", "list_epics",
            "list_labels", "list_llm_tasks", "list_members", "list_projects", "list_tasks", "rerank_epic_group",
            "set_epic_group", "set_subtask_finished", "set_task_assignees", "set_task_epic", "set_task_llm_work",
            "set_task_schedule", "set_task_status", "update_epic", "update_epic_group", "update_subtask", "update_task",
        ], names);
    }

    [SkippableFact]
    public async Task A_notification_is_acknowledged_with_no_body()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var response = await McpClient(token.Value).PostAsync(Endpoint, Rpc(null, "notifications/initialized"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task Get_reports_that_there_is_no_event_stream()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var response = await McpClient(token.Value).GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        // Allow is a content header as far as HttpClient is concerned.
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    // -----------------------------------------------------------------------
    // Tools. The point of the loopback hop is that a tool call is the same
    // request a browser would make — same authorization, same audit, same
    // broadcast — so these assert against core's own endpoints afterwards.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task A_tool_call_creates_a_task_as_the_tokens_owner()
    {
        postgres.SkipUnlessDockerAvailable();

        var (project, board) = await CreateProjectWithBoardAsync("Tools", "TOOL", "MAIN");
        var token = await MintTokenAsync();

        var created = await CallToolAsync(McpClient(token.Value), "create_task", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
            ["name"] = "Write the release notes",
        });
        Assert.Equal("Write the release notes", created["name"]!.GetValue<string>());

        // Core sees it, which means it went through core's own write path.
        var tasks = await _admin.GetJson<List<TaskDto>>($"/api/boards/{board.Id}/tasks");
        var task = Assert.Single(tasks);
        Assert.Equal("Write the release notes", task.Name);
        Assert.Equal(project.Id, board.ProjectId);
    }

    /// <summary>
    /// The labels an agent is shown must be ones it can use. A board carries its
    /// own copies of the project defaults, and listing the defaults instead handed
    /// back ids that every task and sub-task write rejected.
    /// </summary>
    [SkippableFact]
    public async Task Listed_labels_are_the_boards_own_and_accepted_on_its_tasks_and_subtasks()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Labels", "LBL", "MAIN");
        var token = await MintTokenAsync();
        var client = McpClient(token.Value);

        var labels = (await CallToolAsync(client, "list_labels", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
        })).AsArray();
        Assert.NotEmpty(labels);
        Assert.All(labels, l => Assert.Equal(board.Id, l!["boardId"]!.GetValue<Guid>()));

        string IdOf(string kind) =>
            labels.First(l => l!["kind"]!.GetValue<string>() == kind)!["id"]!.GetValue<string>();

        var task = await CallToolAsync(client, "create_task", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
            ["name"] = "Labelled",
            ["labelId"] = IdOf("Task"),
        });
        Assert.Equal(IdOf("Task"), task["labelId"]!.GetValue<string>());

        var subTask = await CallToolAsync(client, "create_subtask", new JsonObject
        {
            ["taskId"] = task["id"]!.GetValue<string>(),
            ["name"] = "Labelled step",
            ["labelId"] = IdOf("SubTask"),
        });
        Assert.Equal(IdOf("SubTask"), subTask["labelId"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Epic_tools_create_edit_and_delete_while_preserving_work()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoardAsync("Epics", "EPICS", "MAIN");
        var client = McpClient((await MintTokenAsync()).Value);
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Village" });
        var sibling = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Sibling", groupId = group.Id });
        var created = await CallToolAsync(client, "create_epic", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["name"] = "Buildings",
            ["description"] = "All the houses", ["groupId"] = group.Id.ToString(),
        });
        var id = created["id"]!.GetValue<string>();
        Assert.Equal(group.Id.ToString(), created["groupId"]!.GetValue<string>());
        Assert.True(created["rank"]!.GetValue<decimal>() > sibling.Rank);
        var ungrouped = await CallToolAsync(client, "create_epic", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["name"] = "Loose",
        });
        Assert.Null(ungrouped["groupId"]);
        Assert.True(ungrouped["rank"]!.GetValue<decimal>() > group.Rank);
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "House", epicId = Guid.Parse(id) });

        // Each edit names one field; the other has to survive core's whole-record PUT.
        var args = new JsonObject { ["boardId"] = board.Id.ToString(), ["epicId"] = id, ["name"] = "Houses" };
        var renamed = await CallToolAsync(client, "update_epic", args);
        Assert.Equal("Houses", renamed["name"]!.GetValue<string>());
        Assert.Equal("All the houses", renamed["description"]!.GetValue<string>());
        args.Remove("name");
        args["description"] = null;
        var cleared = await CallToolAsync(client, "update_epic", args);
        Assert.Equal("Houses", cleared["name"]!.GetValue<string>());
        Assert.Null(cleared["description"]);

        var deleted = await CallToolAsync(client, "delete_epic", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["epicId"] = id,
        });
        Assert.True(deleted["deleted"]!.GetValue<bool>());
        var detail = await _admin.GetJson<BoardDetailDto>($"/api/boards/{board.Id}");
        Assert.DoesNotContain(detail.Epics, e => e.Id == Guid.Parse(id));
        Assert.Equal(2, detail.Epics.Count);
        Assert.Single(detail.Groups);
        Assert.Null(detail.Tasks.Single(t => t.Id == task.Id).EpicId);
    }

    [SkippableFact]
    public async Task Epic_tools_refuse_readers_and_ids_from_another_board()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoardAsync("Epic access", "EPACC", "MAIN");
        var (_, other) = await CreateProjectWithBoardAsync("Epic foreign", "EPFOR", "MAIN");
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Kept", description = "Unchanged" });
        var foreign = await _admin.PostJson<EpicGroupDto>($"/api/boards/{other.Id}/groups", new { name = "Foreign" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members", new { email = "reader@example.com", role = "Read" });
        var reader = McpClient((await MintTokenAsync(client: _factory.As("reader", "reader@example.com"))).Value);
        foreach (var tool in new[] { "create_epic", "update_epic", "delete_epic" })
        {
            var result = await CallToolRawAsync(reader, tool, new JsonObject
            {
                ["boardId"] = board.Id.ToString(), ["epicId"] = epic.Id.ToString(), ["name"] = "Denied",
            });
            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("403", result["content"]![0]!["text"]!.GetValue<string>());
        }

        // The writer can reach both boards, so only the board check stands
        // between a mispaired id and the wrong epic.
        var writer = McpClient((await MintTokenAsync()).Value);
        foreach (var tool in new[] { "update_epic", "delete_epic" })
        {
            var result = await CallToolRawAsync(writer, tool, new JsonObject
            {
                ["boardId"] = other.Id.ToString(), ["epicId"] = epic.Id.ToString(), ["name"] = "Mispaired",
            });
            Assert.True(result["isError"]!.GetValue<bool>());
        }
        var misgrouped = await CallToolRawAsync(writer, "create_epic", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["name"] = "Stray", ["groupId"] = foreign.Id.ToString(),
        });
        Assert.True(misgrouped["isError"]!.GetValue<bool>());
        var kept = Assert.Single(await _admin.GetJson<List<EpicDto>>($"/api/boards/{board.Id}/epics"));
        Assert.Equal("Kept", kept.Name);
        Assert.Equal("Unchanged", kept.Description);
    }

    [SkippableFact]
    public async Task Group_tools_create_rename_rank_and_delete_while_preserving_work()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoardAsync("Groups", "GROUPS", "MAIN");
        var client = McpClient((await MintTokenAsync()).Value);
        Assert.Empty((await CallToolAsync(client, "list_epic_groups", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
        })).AsArray());
        var root = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Root" });
        var group = await CallToolAsync(client, "create_epic_group", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["name"] = "Village 1",
        });
        var id = group["id"]!.GetValue<string>();
        Assert.True(group["rank"]!.GetValue<decimal>() > root.Rank);
        var first = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Buildings", groupId = id });
        var second = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "People", groupId = id });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "House", epicId = first.Id });
        var adHoc = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Ad hoc" });
        var renamed = await CallToolAsync(client, "update_epic_group", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["groupId"] = id, ["name"] = "Village One",
        });
        Assert.Equal("Village One", renamed["name"]!.GetValue<string>());
        var ranked = await CallToolAsync(client, "rerank_epic_group", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["groupId"] = id, ["followingId"] = root.Id.ToString(),
        });
        Assert.True(ranked["rank"]!.GetValue<decimal>() < root.Rank);
        var listed = Assert.Single((await CallToolAsync(client, "list_epic_groups", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
        })).AsArray());
        Assert.Equal("Village One", listed!["name"]!.GetValue<string>());
        var deleted = await CallToolAsync(client, "delete_epic_group", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["groupId"] = id,
        });
        Assert.True(deleted["deleted"]!.GetValue<bool>());
        var detail = await _admin.GetJson<BoardDetailDto>($"/api/boards/{board.Id}");
        Assert.Empty(detail.Groups);
        Assert.Equal(new[] { first.Id, second.Id, root.Id }, detail.Epics.Select(e => e.Id));
        Assert.All(detail.Epics, e => Assert.Null(e.GroupId));
        Assert.Equal(first.Id, detail.Tasks.Single(t => t.Id == task.Id).EpicId);
        Assert.Null(detail.Tasks.Single(t => t.Id == adHoc.Id).EpicId);
    }

    [SkippableFact]
    public async Task Epic_group_moves_append_reorder_transfer_and_ungroup()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoardAsync("Moves", "MOVES", "MAIN");
        var client = McpClient((await MintTokenAsync()).Value);
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "One" });
        var other = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Two" });
        var child = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Child", groupId = group.Id });
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Moving" });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Work", epicId = epic.Id });
        var args = new JsonObject { ["boardId"] = board.Id.ToString(), ["epicId"] = epic.Id.ToString(), ["groupId"] = group.Id.ToString() };
        var moved = await CallToolAsync(client, "set_epic_group", args);
        Assert.Equal(group.Id.ToString(), moved["groupId"]!.GetValue<string>());
        Assert.True(moved["rank"]!.GetValue<decimal>() > child.Rank);
        args["followingId"] = child.Id.ToString();
        moved = await CallToolAsync(client, "set_epic_group", args);
        Assert.True(moved["rank"]!.GetValue<decimal>() < child.Rank);
        args.Remove("followingId");
        args["groupId"] = other.Id.ToString();
        moved = await CallToolAsync(client, "set_epic_group", args);
        Assert.Equal(other.Id.ToString(), moved["groupId"]!.GetValue<string>());
        args["groupId"] = null;
        moved = await CallToolAsync(client, "set_epic_group", args);
        Assert.Null(moved["groupId"]);
        Assert.True(moved["rank"]!.GetValue<decimal>() > other.Rank);
        args["precedingId"] = null;
        args["followingId"] = null;
        moved = await CallToolAsync(client, "set_epic_group", args);
        Assert.True(moved["rank"]!.GetValue<decimal>() < group.Rank);
        Assert.Equal(epic.Id, (await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).EpicId);
        var listed = await CallToolAsync(client, "list_epics", new JsonObject { ["boardId"] = board.Id.ToString() });
        Assert.Equal(group.Id.ToString(), listed.AsArray().Single(e => e!["id"]!.GetValue<string>() == child.Id.ToString())!["groupId"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Group_tools_enforce_read_and_write_permissions()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoardAsync("Access", "ACCESS", "MAIN");
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Village" });
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Epic" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members", new { email = "reader@example.com", role = "Read" });
        var reader = _factory.As("reader", "reader@example.com");
        var client = McpClient((await MintTokenAsync(client: reader)).Value);
        Assert.Single((await CallToolAsync(client, "list_epic_groups", new JsonObject { ["boardId"] = board.Id.ToString() })).AsArray());
        foreach (var tool in new[] { "create_epic_group", "update_epic_group", "delete_epic_group", "rerank_epic_group", "set_epic_group" })
        {
            var result = await CallToolRawAsync(client, tool, new JsonObject
            {
                ["boardId"] = board.Id.ToString(), ["groupId"] = group.Id.ToString(),
                ["epicId"] = epic.Id.ToString(), ["name"] = "Denied", ["precedingId"] = epic.Id.ToString(),
            });
            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("403", result["content"]![0]!["text"]!.GetValue<string>());
        }
        var (_, privateBoard) = await CreateProjectWithBoardAsync("Private groups", "PRIVATE", "MAIN");
        var refused = await CallToolRawAsync(client, "list_epic_groups", new JsonObject { ["boardId"] = privateBoard.Id.ToString() });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("403", refused["content"]![0]!["text"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Group_tools_reject_invalid_destinations_without_moving_work()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoardAsync("Local", "LOCAL", "MAIN");
        var (_, other) = await CreateProjectWithBoardAsync("Foreign", "FOREIGN", "MAIN");
        var group = await _admin.PostJson<EpicGroupDto>($"/api/boards/{board.Id}/groups", new { name = "Local" });
        var foreign = await _admin.PostJson<EpicGroupDto>($"/api/boards/{other.Id}/groups", new { name = "Foreign" });
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{board.Id}/epics", new { name = "Epic", groupId = group.Id });
        var client = McpClient((await MintTokenAsync()).Value);
        var args = new JsonObject { ["boardId"] = board.Id.ToString(), ["epicId"] = epic.Id.ToString() };
        foreach (var invalid in new[] { "missing", "", "not-a-guid", foreign.Id.ToString() })
        {
            if (invalid != "missing") args["groupId"] = invalid;
            var result = await CallToolRawAsync(client, "set_epic_group", args);
            Assert.True(result["isError"]!.GetValue<bool>());
        }
        args["groupId"] = group.Id.ToString();
        args["followingId"] = foreign.Id.ToString();
        Assert.True((await CallToolRawAsync(client, "set_epic_group", args))["isError"]!.GetValue<bool>());
        args.Remove("followingId");
        args["boardId"] = other.Id.ToString();
        Assert.True((await CallToolRawAsync(client, "set_epic_group", args))["isError"]!.GetValue<bool>());
        var invalidRank = await CallToolRawAsync(client, "rerank_epic_group", new JsonObject
        {
            ["boardId"] = board.Id.ToString(), ["groupId"] = group.Id.ToString(), ["precedingId"] = foreign.Id.ToString(),
        });
        Assert.True(invalidRank["isError"]!.GetValue<bool>());
        var invalidDelete = await CallToolRawAsync(client, "delete_epic_group", new JsonObject
        {
            ["boardId"] = other.Id.ToString(), ["groupId"] = group.Id.ToString(),
        });
        Assert.True(invalidDelete["isError"]!.GetValue<bool>());
        var detail = await _admin.GetJson<BoardDetailDto>($"/api/boards/{board.Id}");
        Assert.Equal(group.Id, Assert.Single(detail.Epics).GroupId);
        Assert.Equal("Local", Assert.Single(detail.Groups).Name);
    }

    [SkippableFact]
    public async Task A_task_can_be_addressed_by_serial_number()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Serials", "SER", "BRD");
        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Findable" });
        var token = await MintTokenAsync();

        var moved = await CallToolAsync(McpClient(token.Value), "set_task_status", new JsonObject
        {
            ["taskId"] = $"SER-BRD-{task.Number}",
            ["status"] = "Finished",
        });

        Assert.Equal("Finished", moved["status"]!.GetValue<string>());
    }

    /// <summary>
    /// An update names only what it changes; core's PUT replaces the whole
    /// record, so everything else has to survive the round trip — including the
    /// deadline, which no tool exposes.
    /// </summary>
    [SkippableFact]
    public async Task An_update_preserves_fields_the_caller_did_not_mention()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Merge", "MRG", "ONE");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new
        {
            name = "Original",
            description = "Original description",
            deadlineAt = "2026-12-24",
        });
        var token = await MintTokenAsync();

        await CallToolAsync(McpClient(token.Value), "update_task", new JsonObject
        {
            ["taskId"] = task.Id.ToString(),
            ["name"] = "Renamed",
        });

        var after = await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}");
        Assert.Equal("Renamed", after.Name);
        Assert.Equal("Original description", after.Description);
        Assert.Equal(new DateOnly(2026, 12, 24), after.DeadlineAt);
    }

    /// <summary>
    /// Explicit null is "clear it", absent is "leave it" — the distinction the
    /// argument readers exist for.
    /// </summary>
    [SkippableFact]
    public async Task An_explicit_null_clears_a_field()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Clearing", "CLR", "ONE");
        var task = await _admin.PostJson<TaskDto>(
            $"/api/boards/{board.Id}/tasks", new { name = "Has a description", description = "Gone soon" });
        var token = await MintTokenAsync();

        await CallToolAsync(McpClient(token.Value), "update_task", new JsonObject
        {
            ["taskId"] = task.Id.ToString(),
            ["description"] = null,
        });

        Assert.Null((await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).Description);
    }

    /// <summary>
    /// The round trip the catalog previously could not make. set_task_llm_work
    /// refuses a task whose assignees have no MCP access, so without an
    /// assignment tool an agent had to stop and ask a human to assign the task
    /// in the browser before it could flag its own work.
    /// </summary>
    [SkippableFact]
    public async Task An_agent_can_create_assign_and_flag_work_without_leaving_mcp()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Assignment", "ASGN", "MAIN");
        var token = await MintTokenAsync();
        var client = McpClient(token.Value);

        var me = (await CallToolAsync(client, "get_me", new JsonObject()))["id"]!.GetValue<Guid>();
        var created = await CallToolAsync(client, "create_task", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
            ["name"] = "Triage the crash reports",
        });
        var taskId = created["id"]!.GetValue<Guid>();

        var assigned = await CallToolAsync(client, "set_task_assignees", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["userIds"] = new JsonArray(me.ToString()),
        });
        Assert.Equal([me], assigned["assigneeIds"]!.AsArray().Select(id => id!.GetValue<Guid>()));

        var flagged = await CallToolAsync(client, "set_task_llm_work", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["llmWork"] = true,
        });
        Assert.True(flagged["llmWork"]!.GetValue<bool>());

        // Core agrees, so the assignment went through core's own write path.
        Assert.Equal([me], (await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}")).AssigneeIds);
    }

    /// <summary>
    /// Assignment replaces rather than adds, and an empty array is how a caller
    /// clears one — which is why "userIds" is required even when it is empty.
    /// </summary>
    [SkippableFact]
    public async Task Setting_assignees_replaces_the_whole_set()
    {
        postgres.SkipUnlessDockerAvailable();

        var (project, board) = await CreateProjectWithBoardAsync("Replacing", "RPL", "MAIN");
        var other = _factory.As("other", "other@example.com", "Other");
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "other@example.com", role = "ReadWrite" });
        var otherId = (await other.GetJson<UserDto>("/api/users/me")).Id;

        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Shared work" });
        var token = await MintTokenAsync();
        var client = McpClient(token.Value);
        var me = (await CallToolAsync(client, "get_me", new JsonObject()))["id"]!.GetValue<Guid>();

        await CallToolAsync(client, "set_task_assignees", new JsonObject
        {
            ["taskId"] = task.Id.ToString(),
            ["userIds"] = new JsonArray(me.ToString(), otherId.ToString()),
        });
        Assert.Equal(
            new HashSet<Guid> { me, otherId },
            (await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).AssigneeIds.ToHashSet());

        // Naming only one of them drops the other.
        await CallToolAsync(client, "set_task_assignees", new JsonObject
        {
            ["taskId"] = task.Id.ToString(),
            ["userIds"] = new JsonArray(otherId.ToString()),
        });
        Assert.Equal([otherId], (await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).AssigneeIds);

        await CallToolAsync(client, "set_task_assignees", new JsonObject
        {
            ["taskId"] = task.Id.ToString(),
            ["userIds"] = new JsonArray(),
        });
        Assert.Empty((await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).AssigneeIds);
    }

    /// <summary>
    /// list_members is what turns a task's assigneeIds into people. Its rows
    /// carry both a membership id and a userId, and only the latter assigns —
    /// a pending invite has none at all until that person first signs in.
    /// </summary>
    [SkippableFact]
    public async Task Members_resolve_assignee_ids_and_a_pending_invite_has_no_user_id()
    {
        postgres.SkipUnlessDockerAvailable();

        var (project, board) = await CreateProjectWithBoardAsync("Members", "MEM", "MAIN");
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "invited@example.com", role = "ReadWrite" });
        var token = await MintTokenAsync();
        var client = McpClient(token.Value);
        var me = (await CallToolAsync(client, "get_me", new JsonObject()))["id"]!.GetValue<Guid>();

        var members = (await CallToolAsync(client, "list_members", new JsonObject
        {
            ["projectId"] = project.Id.ToString(),
        })).AsArray();

        var mine = Assert.Single(members, m => m!["userId"]?.GetValue<Guid>() == me);
        Assert.Equal("admin@example.com", mine!["email"]!.GetValue<string>());
        var pending = Assert.Single(members, m => m!["email"]!.GetValue<string>() == "invited@example.com");
        Assert.True(pending!["isPending"]!.GetValue<bool>());
        Assert.Null(pending["userId"]?.GetValue<Guid?>());

        // The membership id is not a user id; assigning by it is refused rather than guessed at.
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Needs an owner" });
        var refused = await CallToolRawAsync(client, "set_task_assignees", new JsonObject
        {
            ["taskId"] = task.Id.ToString(),
            ["userIds"] = new JsonArray(mine["id"]!.GetValue<Guid>().ToString()),
        });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("Invalid assignees", refused["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty((await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).AssigneeIds);
    }

    /// <summary>
    /// A deadline is a calendar day. It can be set at creation, changed by
    /// naming it, and cleared only by an explicit null — an omitted deadline
    /// still survives the read-modify-write.
    /// </summary>
    [SkippableFact]
    public async Task A_deadline_is_set_changed_and_cleared_only_when_named()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Deadlines", "DUE", "MAIN");
        var token = await MintTokenAsync();
        var client = McpClient(token.Value);

        var created = await CallToolAsync(client, "create_task", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
            ["name"] = "Ship the beta",
            ["deadlineAt"] = "2026-12-24",
        });
        var taskId = created["id"]!.GetValue<Guid>();
        Assert.Equal(new DateOnly(2026, 12, 24), (await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}")).DeadlineAt);

        await CallToolAsync(client, "update_task", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["deadlineAt"] = "2027-01-15",
        });
        Assert.Equal(new DateOnly(2027, 1, 15), (await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}")).DeadlineAt);

        await CallToolAsync(client, "update_task", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["name"] = "Ship the release candidate",
        });
        Assert.Equal(new DateOnly(2027, 1, 15), (await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}")).DeadlineAt);

        await CallToolAsync(client, "update_task", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["deadlineAt"] = null,
        });
        Assert.Null((await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}")).DeadlineAt);
    }

    [SkippableFact]
    public async Task A_start_date_is_set_through_create_and_the_schedule_tool()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Ranges", "RNG", "MAIN");
        var token = await MintTokenAsync();
        var client = McpClient(token.Value);

        var created = await CallToolAsync(client, "create_task", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
            ["name"] = "Sprint work",
            ["startAt"] = "2026-12-01",
            ["deadlineAt"] = "2026-12-14",
        });
        var taskId = created["id"]!.GetValue<Guid>();
        var task = await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}");
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 14)), (task.StartAt, task.DeadlineAt));

        // Naming only the deadline keeps the start.
        await CallToolAsync(client, "set_task_schedule", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["deadlineAt"] = "2026-12-20",
        });
        task = await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}");
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 20)), (task.StartAt, task.DeadlineAt));

        await CallToolAsync(client, "set_task_schedule", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["startAt"] = null,
        });
        Assert.Null((await _admin.GetJson<TaskDto>($"/api/tasks/{taskId}")).StartAt);

        // Core's rule surfaces as a readable tool error rather than a guess.
        var refused = await CallToolRawAsync(client, "set_task_schedule", new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["startAt"] = "2027-01-01",
        });
        Assert.True(refused["isError"]!.GetValue<bool>());
    }

    /// <summary>
    /// An ambiguous date is rejected here rather than guessed at: "01/02/2026"
    /// is two different days depending on who wrote it.
    /// </summary>
    [SkippableFact]
    public async Task A_deadline_that_is_not_a_calendar_date_is_a_readable_error()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Bad dates", "BAD", "MAIN");
        var token = await MintTokenAsync();

        var result = await CallToolRawAsync(McpClient(token.Value), "create_task", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
            ["name"] = "Someday",
            ["deadlineAt"] = "next Friday",
        });

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("YYYY-MM-DD", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(await _admin.GetJson<List<TaskDto>>($"/api/boards/{board.Id}/tasks"));
    }

    /// <summary>
    /// The authorization test. A tool call is a real API request, so a token
    /// reaches exactly what its owner reaches — nothing more.
    /// </summary>
    [SkippableFact]
    public async Task A_token_cannot_reach_a_project_its_owner_is_not_a_member_of()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, board) = await CreateProjectWithBoardAsync("Private", "PRIV", "MAIN");

        // A signed-in stranger, with their own token.
        var stranger = _factory.As("stranger", "stranger@example.com", "Stranger");
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{board.ProjectId}/members",
            new { email = "stranger@example.com", role = "Read" });
        var strangerToken = await MintTokenAsync(client: stranger);

        // Membership granted: the board is visible.
        var visible = await CallToolAsync(McpClient(strangerToken.Value), "list_boards", new JsonObject
        {
            ["projectId"] = board.ProjectId.ToString(),
        });
        Assert.Single(visible.AsArray());

        // A second project they were never invited to stays invisible.
        var (secret, _) = await CreateProjectWithBoardAsync("Secret", "SECR", "MAIN");
        var refused = await CallToolRawAsync(McpClient(strangerToken.Value), "list_boards", new JsonObject
        {
            ["projectId"] = secret.Id.ToString(),
        });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("403", refused["content"]![0]!["text"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task A_tool_failure_is_a_result_the_model_can_read()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var result = await CallToolRawAsync(McpClient(token.Value), "get_task", new JsonObject
        {
            ["taskId"] = "not-a-serial",
        });

        // isError, not a JSON-RPC error: the conversation has to be able to recover.
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("DMD-PXW26-2", result["content"]![0]!["text"]!.GetValue<string>());
    }

    // -----------------------------------------------------------------------
    // Admin audit
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task The_audit_shows_usage_but_never_a_token()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync("Laptop");
        await ListToolsAsync(McpClient(token.Value));

        var audit = await _admin.GetJson<JsonObject>($"{Tokens}/admin");
        var text = audit.ToJsonString();
        Assert.DoesNotContain(token.Value, text, StringComparison.Ordinal);
        // Nor the hash: the audit is metadata, and offers nothing to replay.
        Assert.DoesNotContain("tokenHash", text, StringComparison.OrdinalIgnoreCase);

        var row = Assert.Single(audit["tokens"]!.AsArray());
        Assert.Equal("admin@example.com", row!["userEmail"]!.GetValue<string>());
        Assert.Equal("Laptop", row["description"]!.GetValue<string>());
        Assert.Null(row["revokedAt"]);

        // 24 dense hourly buckets, with this run's traffic in the newest.
        var usage = row["usage"]!.AsArray().Select(v => v!.GetValue<int>()).ToList();
        Assert.Equal(24, usage.Count);
        Assert.True(usage[^1] > 0, "the current hour should have counted the requests just made");
    }

    /// <summary>
    /// The extension authenticates ahead of core's rate limiter, so whatever a
    /// lookup costs, a request the limiter then throttles has already paid it.
    /// A burst on one token, or one bogus token replayed, costs one lookup, and
    /// usage is counted in memory rather than written per request.
    /// </summary>
    [SkippableFact]
    public async Task Repeated_requests_cost_one_lookup_and_no_usage_writes()
    {
        postgres.SkipUnlessDockerAvailable();

        var commands = new CommandLog();
        using var observed = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(commands)));
        using var admin = observed.As("admin", "admin@example.com", "Admin");
        var token = await MintTokenAsync("Burst", admin);
        using var valid = observed.CreateClient();
        valid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        using var bogus = observed.CreateClient();
        bogus.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "solomcp_not-a-real-token");

        commands.Clear();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await valid.GetAsync("/api/settings")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await bogus.GetAsync("/api/settings")).StatusCode);
        }

        Assert.Equal(2, commands.Matching("\"TokenHash\""));
        Assert.Equal(0, commands.Matching("\"TokenUsage\""));

        // Reading the graph writes out what was counted, so it is never behind.
        var audit = await admin.GetJson<JsonObject>($"{Tokens}/admin");
        var row = audit["tokens"]!.AsArray().Single(t => t!["id"]!.GetValue<Guid>() == token.Id)!;
        Assert.Equal(5, row["usage"]!.AsArray()[^1]!.GetValue<int>());
        Assert.NotNull(row["lastUsedAt"]);

        // And a cached token still dies the moment it's revoked.
        var revoke = await admin.PostAsync($"{Tokens}/admin/{token.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await valid.GetAsync("/api/settings")).StatusCode);
    }

    /// <summary>EF's executed commands, as one host logs them.</summary>
    private sealed class CommandLog : ILoggerProvider
    {
        private readonly List<string> _commands = [];

        public int Matching(string text)
        {
            lock (_commands)
            {
                return _commands.Count(c => c.Contains(text, StringComparison.Ordinal));
            }
        }

        public void Clear()
        {
            lock (_commands)
            {
                _commands.Clear();
            }
        }

        public ILogger CreateLogger(string categoryName) =>
            categoryName == "Microsoft.EntityFrameworkCore.Database.Command" ? new Logger(this) : NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(CommandLog log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (log._commands)
                {
                    log._commands.Add(formatter(state, exception));
                }
            }
        }
    }

    /// <summary>
    /// The proportionate lever beside revoke-all: one person's access, without
    /// costing everyone else theirs.
    /// </summary>
    [SkippableFact]
    public async Task An_admin_can_revoke_one_persons_token_without_touching_the_rest()
    {
        postgres.SkipUnlessDockerAvailable();

        var mine = await MintTokenAsync();
        var stranger = await OtherMemberAsync();
        var theirs = await MintTokenAsync(client: stranger);

        var audit = await _admin.GetJson<JsonObject>($"{Tokens}/admin");
        var theirRow = audit["tokens"]!.AsArray()
            .Single(t => t!["userEmail"]!.GetValue<string>() == "stranger@example.com")!;
        var theirId = Guid.Parse(theirRow["id"]!.GetValue<string>());

        var response = await _admin.PostAsync($"{Tokens}/admin/{theirId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ListToolsAsync(McpClient(theirs.Value))).Status);
        // Everyone else is untouched — that is the whole point of not using revoke-all.
        Assert.Equal(HttpStatusCode.OK, (await ListToolsAsync(McpClient(mine.Value))).Status);

        var after = await _admin.GetJson<JsonObject>($"{Tokens}/admin");
        var revoked = after["tokens"]!.AsArray()
            .Single(t => Guid.Parse(t!["id"]!.GetValue<string>()) == theirId)!;
        Assert.Equal("RevokedByAdmin", revoked["revokedReason"]!.GetValue<string>());
    }

    /// <summary>
    /// Idempotent, and it must not move the audit's account of when access
    /// actually stopped.
    /// </summary>
    [SkippableFact]
    public async Task Revoking_an_already_revoked_token_leaves_the_original_record()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var id = token.Id;

        await _admin.DeleteAsync($"{Tokens}/current");
        var first = await _admin.GetJson<JsonObject>($"{Tokens}/admin");
        var before = first["tokens"]!.AsArray().Single(t => Guid.Parse(t!["id"]!.GetValue<string>()) == id)!;

        var response = await _admin.PostAsync($"{Tokens}/admin/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var second = await _admin.GetJson<JsonObject>($"{Tokens}/admin");
        var after = second["tokens"]!.AsArray().Single(t => Guid.Parse(t!["id"]!.GetValue<string>()) == id)!;

        Assert.Equal(before["revokedAt"]!.GetValue<string>(), after["revokedAt"]!.GetValue<string>());
        Assert.Equal("RevokedByOwner", after["revokedReason"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Revoking_an_unknown_token_is_a_404()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _admin.PostAsync($"{Tokens}/admin/{Guid.NewGuid()}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Core's "sign out everywhere" reaches this extension's tokens through
    /// <c>ISoloCredentialIssuer</c>, so one action in Admin settings cuts off a
    /// person's MCP access along with their desktop sessions.
    /// </summary>
    [SkippableFact]
    public async Task Signing_a_user_out_everywhere_revokes_their_token()
    {
        postgres.SkipUnlessDockerAvailable();

        var mine = await MintTokenAsync();
        var stranger = await OtherMemberAsync();
        var strangerUser = await stranger.GetJson<UserDto>("/api/users/me");
        var theirs = await MintTokenAsync(client: stranger);

        var result = await _admin.PostJson<SignOutEverywhereResultDto>(
            $"/api/admin/users/{strangerUser.Id}/sign-out-everywhere", new { });
        Assert.Equal(new RevokedCredentialsDto("mcp", 1), Assert.Single(result.Extensions));

        Assert.Equal(HttpStatusCode.Unauthorized, (await ListToolsAsync(McpClient(theirs.Value))).Status);
        Assert.Equal(HttpStatusCode.OK, (await ListToolsAsync(McpClient(mine.Value))).Status);

        var audit = await _admin.GetJson<JsonObject>($"{Tokens}/admin");
        var theirRow = audit["tokens"]!.AsArray()
            .Single(t => Guid.Parse(t!["id"]!.GetValue<string>()) == theirs.Id)!;
        Assert.Equal("RevokedByAdmin", theirRow["revokedReason"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Revoke_all_cuts_off_every_token_on_the_instance()
    {
        postgres.SkipUnlessDockerAvailable();

        var mine = await MintTokenAsync();
        var stranger = await OtherMemberAsync();
        var theirs = await MintTokenAsync(client: stranger);

        var result = await _admin.PostJson<JsonObject>(
            $"{Tokens}/admin/revoke-all", new { confirm = "revoke all tokens" });
        Assert.Equal(2, result["revoked"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await ListToolsAsync(McpClient(mine.Value))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ListToolsAsync(McpClient(theirs.Value))).Status);

        var audit = await _admin.GetJson<JsonObject>($"{Tokens}/admin");
        Assert.All(audit["tokens"]!.AsArray(),
            row => Assert.Equal("RevokedByAdmin", row!["revokedReason"]!.GetValue<string>()));
    }

    [SkippableFact]
    public async Task Revoke_all_refuses_a_mistyped_confirmation()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var response = await _admin.PostAsJsonAsync(
            $"{Tokens}/admin/revoke-all", new { confirm = "yes" }, TestClients.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ListToolsAsync(McpClient(token.Value))).Status);
    }

    [SkippableFact]
    public async Task The_audit_is_super_admin_only()
    {
        postgres.SkipUnlessDockerAvailable();

        var stranger = await OtherMemberAsync();
        await MintTokenAsync(client: stranger);

        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"{Tokens}/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsJsonAsync(
            $"{Tokens}/admin/revoke-all", new { confirm = "revoke all tokens" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.PostAsync($"{Tokens}/admin/{Guid.NewGuid()}/revoke", null)).StatusCode);
    }

    // -----------------------------------------------------------------------
    // The client half. The inventory and the assembly are for signed-in users:
    // an extension page is never reachable before sign-in, because a manifest
    // can only ask for a level of *project* access. content/ is the exception,
    // and a structural one — a page links it with a plain <link>, which carries
    // no Authorization header.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task The_client_half_is_advertised_with_its_nav_entry()
    {
        postgres.SkipUnlessDockerAvailable();

        var extensions = await _admin.GetJson<List<ClientExtensionDto>>("/api/extensions");

        var mcp = Assert.Single(extensions, e => e.Id == "mcp");
        Assert.Equal("Solo.Mcp.Client", mcp.ClientAssembly);
        Assert.False(string.IsNullOrWhiteSpace(mcp.SoloClientVersion));

        // Instance-level: no {projectAlias}, so the sidebar renders one of it.
        var entry = Assert.Single(mcp.Nav);
        Assert.Equal("/mcp", entry.Route);
        Assert.Equal("MCP access", entry.Label);
        Assert.DoesNotContain("{projectAlias}", entry.Route, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_client_assembly_is_served_to_a_signed_in_caller_and_no_one_else()
    {
        postgres.SkipUnlessDockerAvailable();

        var assembly = await _admin.GetAsync("/api/extensions/mcp/client/Solo.Mcp.Client.dll");
        Assert.Equal(HttpStatusCode.OK, assembly.StatusCode);
        var bytes = await assembly.Content.ReadAsByteArrayAsync();
        // A PE image, not an error page or the SPA shell.
        Assert.Equal("MZ"u8.ToArray(), bytes[..2]);

        // A .NET assembly decompiles, so an anonymous visitor must not get one —
        // nor the inventory that would tell them it exists.
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/extensions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/extensions/mcp/client/Solo.Mcp.Client.dll")).StatusCode);
    }

    /// <summary>
    /// The page pulls its stylesheet with a plain <c>&lt;link&gt;</c>. A
    /// subresource fetch carries no Authorization header and Solo has no cookie
    /// to fall back on, so <c>content/</c> stays anonymous — the same constraint
    /// that keeps a theme's assets anonymous.
    /// </summary>
    [SkippableFact]
    public async Task Package_content_is_served_anonymously_because_a_link_tag_cannot_authenticate()
    {
        postgres.SkipUnlessDockerAvailable();

        var anonymous = _factory.CreateClient();

        var stylesheet = await anonymous.GetAsync("/api/extensions/mcp/content/mcp.css");
        Assert.Equal(HttpStatusCode.OK, stylesheet.StatusCode);
        Assert.Equal("text/css", stylesheet.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".mcp-usage", await stylesheet.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

        // -----------------------------------------------------------------------
    // Storage. The whole point of the correction this package embodies: MCP
    // data lives in the extension's schema, and core's stays untouched.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task Everything_it_stores_lives_in_its_own_schema()
    {
        postgres.SkipUnlessDockerAvailable();

        await MintTokenAsync();

        var extensions = await _admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        var registration = Assert.Single(extensions.Extensions, e => e.ExtensionId == "mcp");
        Assert.Equal("ext_mcp", registration.SchemaName);
        Assert.Equal("Loaded", registration.State);
    }

    [SkippableFact]
    public async Task Mcp_changes_keep_the_user_and_record_their_source_in_the_audit()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoardAsync("Attribution", "ATTR", "MAIN");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Human created" });
        var me = await _admin.GetJson<UserDto>("/api/users/me");
        var token = await MintTokenAsync();
        await CallToolAsync(McpClient(token.Value), "update_task", new JsonObject
        {
            ["taskId"] = task.Id.ToString(), ["name"] = "Agent edited",
        });
        var audit = await _admin.GetJson<PagedResult<AuditEntryDto>>($"/api/projects/{project.Id}/audit");
        var edits = audit.Items.Where(e => e.RowId == task.Id.ToString() && e.ColumnName == "Name").ToList();
        Assert.NotEmpty(edits);
        Assert.All(edits, e => { Assert.Equal(me.Id, e.UserId); Assert.Equal("LLM via MCP", e.Annotations.GetValueOrDefault("mcp")); });
        Assert.Contains(audit.Items, e => e.RowId == task.Id.ToString() && e.ColumnName == null && e.Annotations.GetValueOrDefault("mcp") == null);
    }

    [SkippableFact]
    public async Task Attribution_covers_creation_deletion_and_extension_field_changes()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoardAsync("All audit paths", "PATH", "MAIN");
        var token = await MintTokenAsync();
        var client = McpClient(token.Value);
        var me = await _admin.GetJson<UserDto>("/api/users/me");
        var task = await client.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Agent work" });
        task = await client.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { me.Id } });
        await client.PutJson<JsonObject>($"/api/ext/mcp/tasks/{task.Id}/llm-work",
            new { llmWork = true, version = task.Version });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tasks/{task.Id}")).StatusCode);
        var audit = await _admin.GetJson<PagedResult<AuditEntryDto>>($"/api/projects/{project.Id}/audit");
        var rows = audit.Items.Where(e => e.RowId == task.Id.ToString()).ToList();
        Assert.Contains(rows, e => e.ColumnName is null);
        Assert.Contains(rows, e => e.ColumnName == "Extensions");
        Assert.Contains(rows, e => e.ColumnName == "Name");
        Assert.All(rows, e =>
        {
            Assert.Equal(me.Id, e.UserId);
            Assert.Equal("LLM via MCP", e.Annotations["mcp"]);
        });
    }

    [SkippableFact]
    public async Task Attribution_storage_is_extension_owned_and_removing_mcp_leaves_core_audits_usable()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoardAsync("Removability", "REM", "MAIN");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Before" });
        var token = await MintTokenAsync();
        await CallToolAsync(McpClient(token.Value), "update_task", new JsonObject
        {
            ["taskId"] = task.Id.ToString(), ["name"] = "After",
        });
        var audit = await _admin.GetJson<PagedResult<AuditEntryDto>>($"/api/projects/{project.Id}/audit");
        var edit = Assert.Single(audit.Items, e => e.RowId == task.Id.ToString() && e.ColumnName == "Name");
        Assert.Equal("LLM via MCP", edit.Annotations["mcp"]);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'AuditEntries' AND column_name = 'ActionSource'
            """, connection))
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        await using (var command = new NpgsqlCommand("""
            SELECT "TokenId" FROM ext_mcp."AuditAttributions" WHERE "AuditEntryId" = @id
            """, connection))
        {
            command.Parameters.AddWithValue("id", edit.Id);
            Assert.Equal(token.Id, await command.ExecuteScalarAsync());
        }

        _factory.Dispose();
        _factory = new SoloApiFactory(_connectionString, TestExtensions.None);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        var withoutMcp = await _admin.GetJson<PagedResult<AuditEntryDto>>($"/api/projects/{project.Id}/audit");
        var preserved = Assert.Single(withoutMcp.Items, e => e.Id == edit.Id);
        Assert.Empty(preserved.Annotations);
        Assert.Equal(edit.UserId, preserved.UserId);
        Assert.Equal("After", (await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).Name);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.GetAsync($"/api/ext/mcp/tasks/{task.Id}/llm-work")).StatusCode);
    }

    [SkippableFact]
    public async Task An_attribution_write_failure_rolls_back_the_action_and_its_core_audit()
    {
        postgres.SkipUnlessDockerAvailable();
        var (project, board) = await CreateProjectWithBoardAsync("Atomic attribution", "ATOM", "MAIN");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Original" });
        var token = await MintTokenAsync();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                DROP TABLE ext_mcp."AuditAttributions"
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
        var failed = await CallToolRawAsync(McpClient(token.Value), "update_task", new JsonObject
        {
            ["taskId"] = task.Id.ToString(), ["name"] = "Must roll back",
        });
        Assert.True(failed["isError"]!.GetValue<bool>());
        Assert.Equal("Original", (await _admin.GetJson<TaskDto>($"/api/tasks/{task.Id}")).Name);
        var audit = await _admin.GetJson<PagedResult<AuditEntryDto>>($"/api/projects/{project.Id}/audit");
        Assert.DoesNotContain(audit.Items, e => e.RowId == task.Id.ToString() && e.ColumnName == "Name");
    }

    [SkippableFact]
    public async Task Llm_work_defaults_false_requires_active_access_and_is_discoverable_by_tools()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoardAsync("LLM work", "WORK", "MAIN");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Implement" });
        var path = $"/api/ext/mcp/tasks/{task.Id}/llm-work";
        var initial = await _admin.GetJson<JsonObject>(path);
        Assert.False(initial["eligible"]!.GetValue<bool>());
        Assert.False(initial["llmWork"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync(path,
            new { llmWork = true, version = task.Version }, TestClients.Json)).StatusCode);
        var me = await _admin.GetJson<UserDto>("/api/users/me");
        task = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { me.Id } });
        Assert.False((await _admin.GetJson<JsonObject>(path))["eligible"]!.GetValue<bool>());
        var token = await MintTokenAsync();
        var eligible = await _admin.GetJson<JsonObject>(path);
        Assert.True(eligible["eligible"]!.GetValue<bool>());
        Assert.False(eligible["llmWork"]!.GetValue<bool>());
        var marked = await _admin.PutJson<JsonObject>(path, new { llmWork = true, version = task.Version });
        Assert.True(marked["llmWork"]!.GetValue<bool>());
        var list = await CallToolAsync(McpClient(token.Value), "list_llm_tasks", new JsonObject
        {
            ["boardId"] = board.Id.ToString(),
        });
        Assert.Single(list.AsArray());
        var details = await CallToolAsync(McpClient(token.Value), "get_task_llm_work", new JsonObject
        {
            ["taskId"] = $"WORK-MAIN-{task.Number}",
        });
        Assert.True(details["llmWork"]!.GetValue<bool>());
        Assert.Contains(me.Id.ToString(), details["assigneeIds"]!.ToJsonString());
        var cleared = await CallToolAsync(McpClient(token.Value), "set_task_llm_work", new JsonObject
        {
            ["taskId"] = task.Id.ToString(), ["llmWork"] = false,
        });
        Assert.False(cleared["llmWork"]!.GetValue<bool>());
        await CallToolAsync(McpClient(token.Value), "set_task_llm_work", new JsonObject
        {
            ["taskId"] = task.Id.ToString(), ["llmWork"] = true,
        });
        await _admin.DeleteAsync($"{Tokens}/current");
        var revoked = await _admin.GetJson<JsonObject>(path);
        Assert.False(revoked["eligible"]!.GetValue<bool>());
        Assert.False(revoked["llmWork"]!.GetValue<bool>());
    }

    [SkippableFact]
    public async Task Llm_work_rejects_stale_edits_and_does_not_transfer_to_another_assignee()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoardAsync("Reassignment", "REAS", "MAIN");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Work" });
        var me = await _admin.GetJson<UserDto>("/api/users/me");
        await MintTokenAsync();
        task = await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { me.Id } });
        var path = $"/api/ext/mcp/tasks/{task.Id}/llm-work";
        await _admin.PutJson<JsonObject>(path, new { llmWork = true, version = task.Version });
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PutAsJsonAsync(path,
            new { llmWork = false, version = task.Version }, TestClients.Json)).StatusCode);
        Assert.True((await _admin.GetJson<JsonObject>(path))["llmWork"]!.GetValue<bool>());

        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{board.ProjectId}/members",
            new { email = "other@example.com", role = "ReadWrite" });
        var other = _factory.As("other", "other@example.com", "Other");
        var otherUser = await other.GetJson<UserDto>("/api/users/me");
        await MintTokenAsync(client: other);
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { otherUser.Id } });
        var reassigned = await _admin.GetJson<JsonObject>(path);
        Assert.True(reassigned["eligible"]!.GetValue<bool>());
        Assert.False(reassigned["llmWork"]!.GetValue<bool>());
    }

    [SkippableFact]
    public async Task Llm_work_obeys_project_permissions()
    {
        postgres.SkipUnlessDockerAvailable();
        var (_, board) = await CreateProjectWithBoardAsync("Permissions", "PERM", "MAIN");
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Work" });
        var path = $"/api/ext/mcp/tasks/{task.Id}/llm-work";
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{board.ProjectId}/members",
            new { email = "reader@example.com", role = "Read" });
        var reader = _factory.As("reader", "reader@example.com", "Reader");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(path,
            new { llmWork = true, version = task.Version }, TestClients.Json)).StatusCode);
        var stranger = _factory.As("stranger", "stranger@example.com", "Stranger");
        Assert.False((await stranger.GetAsync(path)).IsSuccessStatusCode);
        Assert.False((await stranger.GetAsync($"/api/ext/mcp/tasks/boards/{board.Id}/llm-work")).IsSuccessStatusCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed record MintedToken(Guid Id, string Value);

    /// <summary>
    /// Someone other than the admin who can hold a token: a read-only member of
    /// a project of their own. Only a provisioned user may mint one, and an
    /// identity nobody invited is not provisioned.
    /// </summary>
    private async Task<HttpClient> OtherMemberAsync()
    {
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Anything", alias = "ANY" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "stranger@example.com", role = "Read" });
        return _factory.As("stranger", "stranger@example.com", "Stranger");
    }

    private async Task<MintedToken> MintTokenAsync(string? description = null, HttpClient? client = null)
    {
        var minted = await (client ?? _admin).PostJson<JsonObject>(Tokens, new { description });
        return new MintedToken(
            Guid.Parse(minted["token"]!["id"]!.GetValue<string>()),
            minted["value"]!.GetValue<string>());
    }

    /// <summary>
    /// A client holding nothing but the token — no test identity headers, so the
    /// only thing that can authenticate it is the extension's own scheme.
    /// </summary>
    private HttpClient McpClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Rpc(int? id, string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null)
        {
            message["id"] = id;
        }

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return new StringContent(message.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
    }

    private async Task<(HttpStatusCode Status, JsonObject? Body)> ListToolsAsync(HttpClient client)
    {
        var response = await client.PostAsync(Endpoint, Rpc(1, "tools/list"));
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        return (response.StatusCode,
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    /// <summary>Sends one request and returns its <c>result</c>, failing loudly on a JSON-RPC error.</summary>
    private static async Task<JsonObject> CallAsync(
        HttpClient client, int id, string method, JsonObject? parameters = null)
    {
        var response = await client.PostAsync(Endpoint, Rpc(id, method, parameters));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{method} -> {(int)response.StatusCode}: {text}\n{TestLogCollector.Drain()}");
        }

        var body = JsonNode.Parse(text)!.AsObject();
        if (body["error"] is { } error)
        {
            throw new InvalidOperationException($"{method} answered a JSON-RPC error: {error.ToJsonString()}");
        }

        return body["result"]!.AsObject();
    }

    /// <summary>The raw <c>tools/call</c> result, so a test can assert on isError.</summary>
    /// <remarks>
    /// The arguments are cloned because a <see cref="JsonNode"/> may have only one
    /// parent: a test that walks an epic through several destinations edits one
    /// argument object and re-sends it, which would otherwise fail on the second call.
    /// </remarks>
    private static Task<JsonObject> CallToolRawAsync(HttpClient client, string tool, JsonObject arguments) =>
        CallAsync(client, 2, "tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments.DeepClone(),
        });

    /// <summary>A successful tool call, with its text content parsed back into JSON.</summary>
    private static async Task<JsonNode> CallToolAsync(HttpClient client, string tool, JsonObject arguments)
    {
        var result = await CallToolRawAsync(client, tool, arguments);
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        if (result["isError"]?.GetValue<bool>() == true)
        {
            throw new InvalidOperationException($"{tool} failed: {text}");
        }

        return JsonNode.Parse(text)!;
    }

    private async Task<(ProjectDto Project, BoardDto Board)> CreateProjectWithBoardAsync(
        string name, string projectAlias, string boardAlias)
    {
        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name, alias = projectAlias });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Board", alias = boardAlias });
        return (project, board);
    }
}
