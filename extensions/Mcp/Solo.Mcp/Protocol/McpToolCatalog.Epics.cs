using System.Text.Json.Nodes;
using Solo.Mcp.Rest;
using Solo.Shared;

namespace Solo.Mcp.Protocol;

public sealed partial class McpToolCatalog
{
    private static IEnumerable<McpTool> EpicTools(SoloRestClient rest)
    {
        var board = Uuid("Board id (see list_boards)");
        var epic = Uuid("Epic on this board (see list_epics)");
        var name = Text($"Epic title (plain text, ≤{FieldLimits.ItemName} chars)", FieldLimits.ItemName);
        var description = MarkdownDescription();

        return
        [
            new McpTool("list_epics", "List epics",
                "Lists epics on a board, including groupId (null when ungrouped) and rank. " +
                "Use list_epic_groups to resolve group names; ranks are local to each group or the board.",
                Schema(("boardId", Uuid("Board id"), true)),
                (args, ct) => rest.GetAsync($"api/boards/{RequireGuid(args, "boardId")}/epics", ct)),

            new McpTool("create_epic", "Create epic",
                "Creates an empty epic at the end of a group, or at the end of the board's groups and " +
                "ungrouped epics when groupId is omitted. Attach tasks with set_task_epic or create_task's epicId.",
                Schema(("boardId", board, true), ("name", name, true), ("description", description, false),
                    ("groupId", Uuid("Epic group on this board (see list_epic_groups); omit to leave it ungrouped"), false)),
                (args, ct) => rest.SendAsync<JsonNode>(HttpMethod.Post,
                    $"api/boards/{RequireGuid(args, "boardId")}/epics",
                    new
                    {
                        name = RequireString(args, "name"),
                        description = OptionalString(args, "description"),
                        groupId = OptionalGuid(args, "groupId"),
                    }, ct)),

            new McpTool("update_epic", "Update epic",
                "Edits an epic's name and/or description. Omitted fields keep their current value; " +
                "pass null to clear the description. Reads the current version before saving; a concurrent " +
                "edit is reported as a conflict. Group and position have their own tool, set_epic_group.",
                Schema(("boardId", board, true), ("epicId", epic, true), ("name", name, false),
                    ("description", Nullable(description), false)),
                async (args, ct) =>
                {
                    var current = await ReadEpicAsync(rest, args, ct);
                    return await rest.SendAsync<JsonNode>(HttpMethod.Put, $"api/epics/{current.Id}", new
                    {
                        name = OptionalString(args, "name") ?? current.Name,
                        description = args.ContainsKey("description")
                            ? OptionalString(args, "description")
                            : current.Description,
                        version = current.Version,
                    }, ct);
                }),

            new McpTool("delete_epic", "Delete epic",
                "Deletes only the epic. Its tasks stay on the board as ad-hoc tasks (epicId null) with their " +
                "status, sub-tasks and assignees intact. The epic's name and description cannot be recovered.",
                Schema(("boardId", board, true), ("epicId", epic, true)),
                async (args, ct) =>
                {
                    // Core's delete takes no version, so this read is not a concurrency
                    // check. It holds the epic to the board the caller named: an id
                    // paired with the wrong board is refused rather than deleted.
                    var current = await ReadEpicAsync(rest, args, ct);
                    await rest.SendAsync<JsonNode>(HttpMethod.Delete, $"api/epics/{current.Id}", null, ct);
                    return new JsonObject { ["deleted"] = true, ["epicId"] = current.Id.ToString() };
                }),
        ];
    }

    // REST-only for the same reason as the group shapes: the epic DTOs gained
    // GroupId with epic groups, and create_epic sends it.
    private sealed record EpicFields(Guid Id, string Name, string? Description, uint Version);

    /// <summary>
    /// Core has no single-epic GET; the board's list is the read half of the
    /// read-modify-write, and doubles as the "is it on this board" check.
    /// </summary>
    private static async Task<EpicFields> ReadEpicAsync(SoloRestClient rest, JsonObject args, CancellationToken ct)
    {
        var boardId = RequireGuid(args, "boardId");
        var epicId = RequireGuid(args, "epicId");
        var epics = await rest.SendAsync<List<EpicFields>>(HttpMethod.Get, $"api/boards/{boardId}/epics", null, ct) ?? [];
        return epics.FirstOrDefault(e => e.Id == epicId)
            ?? throw new McpToolException("That board has no epic with this id.");
    }
}
