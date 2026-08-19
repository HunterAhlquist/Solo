using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solo.Shared.Dtos;
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

    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        var connectionString = await postgres.CreateDatabaseAsync("mcp");
        _factory = new SoloApiFactory(connectionString, TestExtensions.Mcp);
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
    public async Task Tools_list_carries_the_ported_tool_set()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var (status, body) = await ListToolsAsync(McpClient(token.Value));
        Assert.Equal(HttpStatusCode.OK, status);

        var names = body!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();

        // The set the desktop shell's local server exposed, one for one.
        Assert.Equal(
        [
            "create_subtask", "create_task", "get_task", "list_boards", "list_epics", "list_labels",
            "list_projects", "list_tasks", "set_subtask_finished", "set_task_epic", "set_task_status",
            "update_subtask", "update_task",
        ], names);
    }

    [SkippableFact]
    public async Task A_notification_is_acknowledged_with_no_body()
    {
        postgres.SkipUnlessDockerAvailable();

        var token = await MintTokenAsync();
        var response = await McpClient(token.Value).PostAsync(Endpoint, Rpc(null, "notifications/initialized"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadAsByteArrayAsync()).Length);
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
    private static Task<JsonObject> CallToolRawAsync(HttpClient client, string tool, JsonObject arguments) =>
        CallAsync(client, 2, "tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments,
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
