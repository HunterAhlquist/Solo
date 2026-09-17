using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task Tools_list_includes_group_management()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var (status, body) = await ListToolsAsync(McpClient(token.Value));
        Assert.Equal(HttpStatusCode.OK, status);

        var names = body!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();

        Assert.Equal(
        [
            "create_epic_group", "create_subtask", "create_task", "delete_epic_group", "get_me", "get_task",
            "get_task_llm_work", "list_boards", "list_epic_groups", "list_epics", "list_labels", "list_llm_tasks",
            "list_members", "list_projects", "list_tasks", "rerank_epic_group", "set_epic_group", "set_subtask_finished",
            "set_task_assignees", "set_task_epic", "set_task_llm_work", "set_task_status",
            "update_epic_group", "update_subtask", "update_task",
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
        // Distinct because core's PUT echoes its tracked navigation, and a
        // freshly added assignment lands in it twice — once from
        // AssignmentHelper.Sync, once from EF's relationship fixup. The stored
        // row is single, as the canonical read below shows.
        Assert.Equal([me], assigned["assigneeIds"]!.AsArray().Select(id => id!.GetValue<Guid>()).Distinct());

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
    /// The proportionate lever beside revoke-all: one person's access, without
    /// costing everyone else theirs.
    /// </summary>
    [SkippableFact]
    public async Task An_admin_can_revoke_one_persons_token_without_touching_the_rest()
    {
        postgres.SkipUnlessDockerAvailable();

        var mine = await MintTokenAsync();
        var stranger = _factory.As("stranger", "stranger@example.com", "Stranger");
        await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Anything", alias = "ANY" });
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

    [SkippableFact]
    public async Task Revoke_all_cuts_off_every_token_on_the_instance()
    {
        postgres.SkipUnlessDockerAvailable();

        var mine = await MintTokenAsync();
        var stranger = _factory.As("stranger", "stranger@example.com", "Stranger");
        await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Anything", alias = "ANY" });
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

        await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Anything", alias = "ANY" });
        var stranger = _factory.As("stranger", "stranger@example.com", "Stranger");
        await MintTokenAsync(client: stranger);

        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"{Tokens}/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsJsonAsync(
            $"{Tokens}/admin/revoke-all", new { confirm = "revoke all tokens" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.PostAsync($"{Tokens}/admin/{Guid.NewGuid()}/revoke", null)).StatusCode);
    }

    // -----------------------------------------------------------------------
    // The client half. The browser fetches all of this before anyone has signed
    // in — the router has to know an extension's routes to render the page
    // behind sign-in at all — so these endpoints must answer anonymously or the
    // whole client half silently never loads.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task The_client_half_is_advertised_with_its_nav_entry()
    {
        postgres.SkipUnlessDockerAvailable();

        var anonymous = _factory.CreateClient();
        var extensions = await anonymous.GetJson<List<ClientExtensionDto>>("/api/extensions");

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
    public async Task The_client_assembly_and_its_content_are_served_anonymously()
    {
        postgres.SkipUnlessDockerAvailable();

        var anonymous = _factory.CreateClient();

        var assembly = await anonymous.GetAsync("/api/extensions/mcp/client/Solo.Mcp.Client.dll");
        Assert.Equal(HttpStatusCode.OK, assembly.StatusCode);
        var bytes = await assembly.Content.ReadAsByteArrayAsync();
        // A PE image, not an error page or the SPA shell.
        Assert.Equal("MZ"u8.ToArray(), bytes[..2]);

        // The page pulls its stylesheet with a plain <link>, which carries no
        // Authorization header — so this one has to be anonymous too.
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
