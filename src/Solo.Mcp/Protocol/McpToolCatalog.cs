using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solo.Mcp.Rest;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Mcp.Protocol;

/// <summary>
/// The tool set, ported from the desktop shell's local MCP server.
/// </summary>
/// <remarks>
/// <para>Read tools walk the hierarchy (projects → boards → tasks) to find ids;
/// task tools also take a full serial number ("DMD-PXW26-2") in place of an id,
/// so a caller holding a serial skips the walk — see <c>ResolveTaskIdAsync</c>.</para>
///
/// <para>Write tools mirror the REST API. Updates are read-modify-write: core's
/// PUTs replace the whole record and demand the current version for optimistic
/// concurrency, so each update fetches first and merges, and callers pass only
/// the fields they mean to change.</para>
/// </remarks>
public sealed partial class McpToolCatalog(SoloRestClient rest, McpPromptCatalog prompts)
{
    public const string Instructions =
        "Before acting, call get_workflow to read the matching Solo workflow: " +
        "solo-create-task for bug reports or requests to track work; solo-implement-task " +
        "before implementing a task flagged for LLM work; solo-summarize-work for what to " +
        "work on next; solo-recent-work for recent or finished work. Follow the returned " +
        "guide, including its reporting limits and presentation instructions. " +
        "Task tools for the Solo project-management app. Hierarchy: projects → " +
        "boards → optional epic groups → epics → tasks → sub-tasks. Groups contain only " +
        "epics; ungrouped epics share the board's rank order with groups, and tasks " +
        "without an epic remain outside all groups. Use list_epic_groups and list_epics " +
        "to discover group and epic ids. Discover task ids with list_projects → list_boards → list_tasks, " +
        "or skip discovery: every taskId parameter also accepts a full serial " +
        "number like \"DMD-PXW26-2\" (project alias, board alias, task number). " +
        "Every call acts as the Solo user whose MCP token authorized it, and " +
        "reaches only the projects that user is a member of. A task's assigneeIds are user " +
        "ids: get_me returns the caller's own, list_members puts names to the rest. " +
        "Use list_llm_tasks to find " +
        "tasks explicitly flagged for LLM work; the flag is metadata, not authorization or an automatic trigger. " +
        "The same guides are also available through prompts/list and prompts/get for clients " +
        "with native prompt support.";

    private readonly IReadOnlyDictionary<string, McpTool> _tools =
        Build(rest, prompts).ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<McpTool> Tools => (IReadOnlyCollection<McpTool>)_tools.Values;

    public McpTool? Find(string name) => _tools.GetValueOrDefault(name);

    // -----------------------------------------------------------------------
    // The catalog
    // -----------------------------------------------------------------------

    private static List<McpTool> Build(SoloRestClient rest, McpPromptCatalog prompts)
    {
        var taskRef = Text("Task: id (GUID) or full serial number like \"DMD-PXW26-2\"");
        var parentRef = Text("Parent task: id (GUID) or full serial number");
        var taskName = Text($"Name (plain text, ≤{FieldLimits.ItemName} chars)", FieldLimits.ItemName);
        var taskDescription = Text(
            $"Description (Markdown, ≤{FieldLimits.Description / 1000}k chars). Link to another task by " +
            "wrapping its serial number in double pipes, e.g. ||DMD-PXW26-2||",
            FieldLimits.Description);
        var deadline = Date("Deadline, a calendar date in YYYY-MM-DD form");

        return
        [
            WorkflowTool(prompts),

            new McpTool("list_projects", "List projects",
                "Lists every Solo project the signed-in user is a member of.",
                Schema(),
                (_, ct) => rest.GetAsync("api/projects", ct)),

            new McpTool("get_me", "Who am I",
                "The Solo user this token belongs to. Its id is what a task's assigneeIds are " +
                "matched against, so an agent assigning work to itself starts here.",
                Schema(),
                (_, ct) => rest.GetAsync("api/users/me", ct)),

            new McpTool("list_members", "List project members",
                "Lists a project's members — the only way to put a name to the assigneeIds a task " +
                "carries. Assignment goes by userId, not the membership id: a pending invite has a " +
                "null userId and cannot be assigned until that person first signs in.",
                Schema(("projectId", Uuid("Project id"), true)),
                (args, ct) => rest.GetAsync($"api/projects/{RequireGuid(args, "projectId")}/members", ct)),

            new McpTool("list_boards", "List boards",
                "Lists the boards of a project.",
                Schema(("projectId", Uuid("Project id"), true)),
                (args, ct) => rest.GetAsync($"api/projects/{RequireGuid(args, "projectId")}/boards", ct)),

            new McpTool("list_epics", "List epics",
                "Lists epics on a board, including groupId (null when ungrouped) and rank. " +
                "Use list_epic_groups to resolve group names; ranks are local to each group or the board.",
                Schema(("boardId", Uuid("Board id"), true)),
                (args, ct) => rest.GetAsync($"api/boards/{RequireGuid(args, "boardId")}/epics", ct)),

            ..GroupTools(rest),

            new McpTool("list_labels", "List labels",
                "Lists a project's labels. A task or sub-task may carry one label (labelId); " +
                "labels are scoped by kind (Task/SubTask) and optionally to a single board.",
                Schema(("projectId", Uuid("Project id"), true)),
                (args, ct) => rest.GetAsync($"api/projects/{RequireGuid(args, "projectId")}/labels", ct)),

            new McpTool("list_tasks", "List tasks",
                "Lists every task on a board in rank order, including status, epic, " +
                "label and sub-tasks.",
                Schema(("boardId", Uuid("Board id"), true)),
                (args, ct) => rest.GetAsync($"api/boards/{RequireGuid(args, "boardId")}/tasks", ct)),

            new McpTool("list_llm_tasks", "List LLM work",
                "Lists flagged tasks with eligible assignee ids on a board. Filter by your user's id " +
                "and task status before choosing work; this metadata does not claim or start a task.",
                Schema(("boardId", Uuid("Board id"), true)),
                (args, ct) => rest.GetAsync($"api/ext/mcp/tasks/boards/{RequireGuid(args, "boardId")}/llm-work", ct)),

            new McpTool("get_task_llm_work", "Get LLM work flag",
                "Reads a task's LLM work flag, flagged assignees and concurrency version.",
                Schema(("taskId", taskRef, true)),
                async (args, ct) => await rest.GetAsync(
                    $"api/ext/mcp/tasks/{await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct)}/llm-work", ct)),

            new McpTool("set_task_llm_work", "Set LLM work flag",
                "Marks or unmarks a task for LLM work. Requires an assignee with active MCP access when enabling.",
                Schema(("taskId", taskRef, true), ("llmWork", Flag("Whether an LLM should perform this work"), true)),
                async (args, ct) =>
                {
                    var id = await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct);
                    var current = await rest.SendAsync<TaskDto>(HttpMethod.Get, $"api/tasks/{id}", null, ct)
                        ?? throw new McpToolException("Task no longer exists.");
                    return await Json(rest.SendAsync<Solo.Mcp.Controllers.LlmWorkDto>(HttpMethod.Put,
                        $"api/ext/mcp/tasks/{id}/llm-work",
                        new { llmWork = RequireBool(args, "llmWork"), version = current.Version }, ct));
                }),

            new McpTool("get_task", "Get a task",
                "Fetches one task by id or serial number, including its sub-tasks.",
                Schema(("taskId", taskRef, true)),
                async (args, ct) => await rest.GetAsync(
                    $"api/tasks/{await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct)}", ct)),

            new McpTool("create_task", "Create a task",
                "Creates a task on a board. New tasks start Unstarted and are appended " +
                "to the end of the board.",
                Schema(
                    ("boardId", Uuid("Board to create the task on"), true),
                    ("name", taskName, true),
                    ("description", taskDescription, false),
                    ("epicId", Uuid("Epic on the same board; omit for an ad-hoc task"), false),
                    ("labelId", Uuid("Task label from the project (see list_labels)"), false),
                    ("deadlineAt", deadline, false)),
                (args, ct) => Json(rest.SendAsync<TaskDto>(
                    HttpMethod.Post,
                    $"api/boards/{RequireGuid(args, "boardId")}/tasks",
                    new CreateTaskRequest
                    {
                        Name = RequireString(args, "name"),
                        Description = OptionalString(args, "description"),
                        EpicId = OptionalGuid(args, "epicId"),
                        LabelId = OptionalGuid(args, "labelId"),
                        DeadlineAt = OptionalDate(args, "deadlineAt"),
                    },
                    ct))),

            new McpTool("update_task", "Update a task",
                "Edits a task's name, description, label and/or deadline. Omitted fields keep " +
                "their current value; pass null to clear the description, label or deadline. " +
                "Status, epic and assignees have their own tools.",
                Schema(
                    ("taskId", taskRef, true),
                    ("name", taskName, false),
                    ("description", Nullable(taskDescription), false),
                    ("labelId", Nullable(Uuid("Task label, or null to clear")), false),
                    ("deadlineAt", Nullable(Date("Deadline (YYYY-MM-DD), or null to clear")), false)),
                (args, ct) => Json(UpdateTaskAsync(rest, args, ct))),

            new McpTool("set_task_status", "Set task status",
                "Moves a task between the Kanban columns.",
                Schema(
                    ("taskId", taskRef, true),
                    ("status", Choice("Target column", nameof(TaskState.Unstarted),
                        nameof(TaskState.InProgress), nameof(TaskState.Finished)), true)),
                async (args, ct) => await Json(rest.SendAsync<TaskDto>(
                    HttpMethod.Put,
                    $"api/tasks/{await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct)}/status",
                    new SetTaskStatusRequest { Status = RequireEnum<TaskState>(args, "status") },
                    ct))),

            new McpTool("set_task_epic", "Set task epic",
                "Attaches a task to an epic on its board, or detaches it to ad-hoc.",
                Schema(
                    ("taskId", taskRef, true),
                    ("epicId", Nullable(Uuid("Epic on the same board, or null to detach")), true)),
                async (args, ct) => await Json(rest.SendAsync<TaskDto>(
                    HttpMethod.Put,
                    $"api/tasks/{await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct)}/epic",
                    new SetTaskEpicRequest { EpicId = OptionalGuid(args, "epicId") },
                    ct))),

            new McpTool("set_task_assignees", "Set task assignees",
                "Replaces a task's assignees. Not a partial edit: anyone left out is unassigned, and " +
                "an empty array clears the task, so read get_task's assigneeIds first when adding " +
                "someone. Users must be activated members of the task's project (see list_members); " +
                "get_me gives an agent its own id.",
                Schema(
                    ("taskId", taskRef, true),
                    ("userIds", UuidList("User ids to assign; empty unassigns everyone"), true)),
                async (args, ct) => await Json(rest.SendAsync<TaskDto>(
                    HttpMethod.Put,
                    $"api/tasks/{await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct)}/assignees",
                    new SetAssigneesRequest { UserIds = RequireGuidList(args, "userIds") },
                    ct))),

            new McpTool("create_subtask", "Create a sub-task",
                "Adds a checklist sub-task to a task.",
                Schema(
                    ("taskId", parentRef, true),
                    ("name", taskName, true),
                    ("labelId", Uuid("Sub-task label from the project (see list_labels)"), false)),
                async (args, ct) => await Json(rest.SendAsync<SubTaskDto>(
                    HttpMethod.Post,
                    $"api/tasks/{await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct)}/subtasks",
                    new CreateSubTaskRequest
                    {
                        Name = RequireString(args, "name"),
                        LabelId = OptionalGuid(args, "labelId"),
                    },
                    ct))),

            new McpTool("update_subtask", "Update a sub-task",
                "Edits a sub-task's name and/or label. Omitted fields keep their " +
                "current value; pass null to clear the label.",
                Schema(
                    ("taskId", parentRef, true),
                    ("subTaskId", Uuid("Sub-task id"), true),
                    ("name", taskName, false),
                    ("labelId", Nullable(Uuid("Sub-task label, or null to clear")), false)),
                (args, ct) => Json(UpdateSubTaskAsync(rest, args, ct))),

            new McpTool("set_subtask_finished", "Finish a sub-task",
                "Checks or unchecks a sub-task.",
                Schema(
                    ("subTaskId", Uuid("Sub-task id"), true),
                    ("isFinished", Flag("true = finished, false = reopen"), true)),
                (args, ct) => Json(rest.SendAsync<SubTaskDto>(
                    HttpMethod.Put,
                    $"api/subtasks/{RequireGuid(args, "subTaskId")}/finished",
                    new SetSubTaskFinishedRequest { IsFinished = RequireBool(args, "isFinished") },
                    ct))),
        ];
    }

    // -----------------------------------------------------------------------
    // Read-modify-write. Core's PUTs replace the whole record, so anything the
    // caller did not mention has to be carried across from the current value.
    // (Dropping one is how the desktop server silently cleared a task's deadline
    // on every edit.)
    // -----------------------------------------------------------------------

    private static async Task<TaskDto?> UpdateTaskAsync(
        SoloRestClient rest, JsonObject args, CancellationToken ct)
    {
        var taskId = await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct);
        var current = await rest.SendAsync<TaskDto>(HttpMethod.Get, $"api/tasks/{taskId}", null, ct)
            ?? throw new McpToolException($"Task {taskId} no longer exists.");

        return await rest.SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}", new UpdateTaskRequest
        {
            Name = OptionalString(args, "name") ?? current.Name,
            Description = args.ContainsKey("description")
                ? OptionalString(args, "description")
                : current.Description,
            LabelId = args.ContainsKey("labelId") ? OptionalGuid(args, "labelId") : current.LabelId,
            DeadlineAt = args.ContainsKey("deadlineAt")
                ? OptionalDate(args, "deadlineAt")
                : current.DeadlineAt,
            Version = current.Version,
        }, ct);
    }

    private static async Task<SubTaskDto?> UpdateSubTaskAsync(
        SoloRestClient rest, JsonObject args, CancellationToken ct)
    {
        var taskId = await ResolveTaskIdAsync(rest, RequireString(args, "taskId"), ct);
        var subTaskId = RequireGuid(args, "subTaskId");

        // Sub-tasks have no GET of their own; the parent's list is the read half
        // of the read-modify-write, and doubles as the "is it really yours" check.
        var siblings = await rest.SendAsync<List<SubTaskDto>>(
            HttpMethod.Get, $"api/tasks/{taskId}/subtasks", null, ct) ?? [];
        var current = siblings.FirstOrDefault(s => s.Id == subTaskId)
            ?? throw new McpToolException("That task has no sub-task with this id.");

        return await rest.SendAsync<SubTaskDto>(
            HttpMethod.Put, $"api/subtasks/{subTaskId}", new UpdateSubTaskRequest
            {
                Name = OptionalString(args, "name") ?? current.Name,
                LabelId = args.ContainsKey("labelId") ? OptionalGuid(args, "labelId") : current.LabelId,
                Version = current.Version,
            }, ct);
    }

    // -----------------------------------------------------------------------
    // Task references. Tools take tasks by GUID or by full serial number —
    // project alias, board alias and per-board task number, e.g. "DMD-PXW26-2".
    // A serial resolves through the board's alias route, whose response carries
    // every task on the board with its number.
    // -----------------------------------------------------------------------

    private static async Task<Guid> ResolveTaskIdAsync(
        SoloRestClient rest, string reference, CancellationToken ct)
    {
        var value = reference.Trim();
        if (Guid.TryParse(value, out var id))
        {
            return id;
        }

        if (!Aliases.TryParseSerial(value, out var projectAlias, out var boardAlias, out var number))
        {
            throw new McpToolException(
                $"\"{reference}\" is neither a task id (GUID) nor a full serial number like \"DMD-PXW26-2\".");
        }

        var board = await rest.SendAsync<BoardDetailDto>(
            HttpMethod.Get,
            $"api/projects/by-alias/{Uri.EscapeDataString(projectAlias)}/boards/{Uri.EscapeDataString(boardAlias)}",
            null,
            ct) ?? throw new McpToolException($"No board {projectAlias}-{boardAlias} is visible to you.");

        var task = board.Tasks.FirstOrDefault(t => t.Number == number)
            ?? throw new McpToolException(
                $"Board {projectAlias}-{boardAlias} has no task #{number}. Numbers are permanent, " +
                "so a missing one means the task was deleted or the serial is mistyped.");

        return task.Id;
    }

    // -----------------------------------------------------------------------
    // Arguments. Absent and explicitly-null are different things for a nullable
    // field — "leave the label alone" versus "clear it" — so callers test
    // ContainsKey first and use the readers below for the value.
    // -----------------------------------------------------------------------

    private static string RequireString(JsonObject args, string name) =>
        OptionalString(args, name) ?? throw new McpToolException($"\"{name}\" is required.");

    private static string? OptionalString(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        var text = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Guid RequireGuid(JsonObject args, string name) =>
        OptionalGuid(args, name) ?? throw new McpToolException($"\"{name}\" is required and must be a GUID.");

    private static Guid? OptionalGuid(JsonObject args, string name)
    {
        var text = OptionalString(args, name);
        if (text is null)
        {
            return null;
        }

        return Guid.TryParse(text, out var id)
            ? id
            : throw new McpToolException($"\"{name}\" must be a GUID, not \"{text}\".");
    }

    private static IReadOnlyList<Guid> RequireGuidList(JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is not JsonArray array)
        {
            throw new McpToolException(
                $"\"{name}\" is required and must be an array of ids — empty, not absent, to clear them.");
        }

        var ids = new List<Guid>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i]?.GetValueKind() != JsonValueKind.String ||
                !Guid.TryParse(array[i]!.GetValue<string>(), out var id))
            {
                throw new McpToolException($"\"{name}[{i}]\" must be a user id (GUID).");
            }

            ids.Add(id);
        }

        return ids;
    }

    /// <summary>A deadline is a calendar day, not an instant — no time zone is involved.</summary>
    private static DateOnly? OptionalDate(JsonObject args, string name)
    {
        var text = OptionalString(args, name);
        if (text is null)
        {
            return null;
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : throw new McpToolException(
                $"\"{name}\" must be a date in YYYY-MM-DD form, not \"{text}\".");
    }

    private static bool RequireBool(JsonObject args, string name)
    {
        if (args.TryGetPropertyValue(name, out var node) &&
            node?.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return node.GetValue<bool>();
        }

        throw new McpToolException($"\"{name}\" is required and must be true or false.");
    }

    private static TEnum RequireEnum<TEnum>(JsonObject args, string name) where TEnum : struct, Enum
    {
        var text = RequireString(args, name);
        return Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new McpToolException(
                $"\"{name}\" must be one of {string.Join(", ", Enum.GetNames<TEnum>())}, not \"{text}\".");
    }

    /// <summary>Re-serializes a typed response so a tool result is the REST payload's own shape.</summary>
    private static async Task<JsonNode?> Json<T>(Task<T?> result) where T : class =>
        JsonSerializer.SerializeToNode(await result, SoloRestClient.Json);

    // -----------------------------------------------------------------------
    // Input schemas. Hand-built JSON Schema rather than reflection over the
    // request DTOs: what a tool accepts is a prompt as much as a contract, and
    // these descriptions are most of what makes a model call it correctly.
    // -----------------------------------------------------------------------

    private static JsonObject Schema(params (string Name, JsonObject Spec, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, spec, isRequired) in properties)
        {
            // Cloned because a JsonNode may have only one parent, and the shared
            // specs above (taskRef, taskName, …) are deliberately reused across
            // tools so their wording cannot drift apart.
            props[name] = spec.DeepClone();
            if (isRequired)
            {
                required.Add(name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject Text(string description, int? maxLength = null)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = description };
        if (maxLength is not null)
        {
            schema["maxLength"] = maxLength;
        }

        return schema;
    }

    private static JsonObject Uuid(string description) =>
        new() { ["type"] = "string", ["format"] = "uuid", ["description"] = description };

    private static JsonObject Date(string description) =>
        new() { ["type"] = "string", ["format"] = "date", ["description"] = description };

    private static JsonObject UuidList(string description) =>
        new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["description"] = description,
        };

    private static JsonObject Flag(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Choice(string description, params string[] values) =>
        new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
            ["description"] = description,
        };

    /// <summary>Widens a schema to accept null — "clear this field" as distinct from "leave it".</summary>
    private static JsonObject Nullable(JsonObject schema)
    {
        var widened = schema.DeepClone().AsObject();
        widened["type"] = new JsonArray("string", "null");
        return widened;
    }
}
