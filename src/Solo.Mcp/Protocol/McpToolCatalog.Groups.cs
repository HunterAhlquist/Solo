using System.Text.Json.Nodes;
using Solo.Mcp.Rest;
using Solo.Shared;

namespace Solo.Mcp.Protocol;

public sealed partial class McpToolCatalog
{
    private static IEnumerable<McpTool> GroupTools(SoloRestClient rest)
    {
        var board = Uuid("Board id (see list_boards)");
        var group = Uuid("Epic group on this board (see list_epic_groups)");
        var name = Text($"Group title (plain text, ≤{FieldLimits.ItemName} chars)", FieldLimits.ItemName);
        var preceding = Nullable(Uuid("Sibling before the new position; a group or ungrouped epic at board level, otherwise an epic in the destination group"));
        var following = Nullable(Uuid("Sibling after the new position; a group or ungrouped epic at board level, otherwise an epic in the destination group"));

        return
        [
            new McpTool("list_epic_groups", "List epic groups",
                "Lists groups on a board in rank order, including names, ids and versions. " +
                "Combine with list_epics and its groupId to find each group's epics. " +
                "Groups and ungrouped epics share the board's top-level rank order.",
                Schema(("boardId", board, true)),
                (args, ct) => rest.GetAsync($"api/boards/{RequireGuid(args, "boardId")}/groups", ct)),

            new McpTool("create_epic_group", "Create epic group",
                "Creates an empty group at the end of the board's groups and ungrouped epics.",
                Schema(("boardId", board, true), ("name", name, true)),
                (args, ct) => rest.SendAsync<JsonNode>(HttpMethod.Post,
                    $"api/boards/{RequireGuid(args, "boardId")}/groups",
                    new { name = RequireString(args, "name") }, ct)),

            new McpTool("update_epic_group", "Rename epic group",
                "Renames a group. Reads its current version before saving; a concurrent edit is reported as a conflict.",
                Schema(("boardId", board, true), ("groupId", group, true), ("name", name, true)),
                async (args, ct) =>
                {
                    var title = RequireString(args, "name");
                    var path = GroupPath(args);
                    var current = await ReadGroupAsync(rest, path, ct);
                    return await rest.SendAsync<JsonNode>(HttpMethod.Put, path,
                        new { name = title, version = current.Version }, ct);
                }),

            new McpTool("delete_epic_group", "Delete epic group",
                "Deletes only the group. Its epics become ungrouped in their current relative order " +
                "at the group's position; all epics and tasks are preserved. Reads the current version before deleting.",
                Schema(("boardId", board, true), ("groupId", group, true)),
                async (args, ct) =>
                {
                    var path = GroupPath(args);
                    var current = await ReadGroupAsync(rest, path, ct);
                    await rest.SendAsync<JsonNode>(HttpMethod.Delete, $"{path}?version={current.Version}", null, ct);
                    return new JsonObject { ["deleted"] = true, ["groupId"] = current.Id.ToString() };
                }),

            new McpTool("rerank_epic_group", "Reorder epic group",
                "Moves a group among this board's groups and ungrouped epics. " +
                "With neither neighbor set, moves to the front. A precedingId takes priority when both are given.",
                Schema(("boardId", board, true), ("groupId", group, true),
                    ("precedingId", preceding, false), ("followingId", following, false)),
                async (args, ct) =>
                {
                    var precedingId = OptionalGuid(args, "precedingId");
                    var followingId = OptionalGuid(args, "followingId");
                    var path = GroupPath(args);
                    var current = await ReadGroupAsync(rest, path, ct);
                    return await rest.SendAsync<JsonNode>(HttpMethod.Put, $"{path}/rank",
                        new { precedingId, followingId, version = current.Version }, ct);
                }),

            new McpTool("set_epic_group", "Set epic group",
                "Moves an epic into a group on the same board, or out to the board when groupId is explicitly null. " +
                "Omit both neighbors to append to the destination. Supply neighbors to choose a position; " +
                "explicitly null neighbors move to the front. A precedingId takes priority. " +
                "Also reorders an epic within its current group. Tasks remain attached to the epic.",
                Schema(("boardId", board, true), ("epicId", Uuid("Epic on this board (see list_epics)"), true),
                    ("groupId", Nullable(Uuid("Destination group on the same board, or null to ungroup")), true),
                    ("precedingId", preceding, false), ("followingId", following, false)),
                (args, ct) => SetEpicGroupAsync(rest, args, ct)),
        ];
    }

    private static string GroupPath(JsonObject args) =>
        $"api/boards/{RequireGuid(args, "boardId")}/groups/{RequireGuid(args, "groupId")}";

    // REST-only shapes keep these tools independent of newer shared DTO members.
    // An older host can still load the package and report a missing endpoint normally.
    private sealed record GroupVersion(Guid Id, uint Version);
    private sealed record EpicPosition(Guid Id, Guid? GroupId, decimal Rank, uint Version);
    private sealed record GroupPosition(Guid Id, decimal Rank);

    private static async Task<GroupVersion> ReadGroupAsync(SoloRestClient rest, string path, CancellationToken ct) =>
        await rest.SendAsync<GroupVersion>(HttpMethod.Get, path, null, ct)
        ?? throw new McpToolException("That epic group no longer exists.");

    private static async Task<JsonNode?> SetEpicGroupAsync(SoloRestClient rest, JsonObject args, CancellationToken ct)
    {
        var boardId = RequireGuid(args, "boardId");
        var epicId = RequireGuid(args, "epicId");
        if (!args.ContainsKey("groupId"))
        {
            throw new McpToolException("\"groupId\" is required; pass null explicitly to ungroup the epic.");
        }

        // Only JSON null means detach: a blank string must not silently ungroup work.
        var groupId = args["groupId"] is null ? (Guid?)null : RequireGuid(args, "groupId");
        var precedingId = OptionalGuid(args, "precedingId");
        var followingId = OptionalGuid(args, "followingId");
        var epics = await rest.SendAsync<List<EpicPosition>>(HttpMethod.Get, $"api/boards/{boardId}/epics", null, ct) ?? [];
        var epic = epics.FirstOrDefault(e => e.Id == epicId)
            ?? throw new McpToolException("That board has no epic with this id.");

        if (!args.ContainsKey("precedingId") && !args.ContainsKey("followingId"))
        {
            var siblings = epics.Where(e => e.Id != epicId && e.GroupId == groupId)
                .Select(e => (e.Id, e.Rank)).ToList();
            if (groupId is null)
            {
                var groups = await rest.SendAsync<List<GroupPosition>>(HttpMethod.Get, $"api/boards/{boardId}/groups", null, ct) ?? [];
                siblings.AddRange(groups.Select(g => (g.Id, g.Rank)));
            }

            precedingId = siblings.OrderBy(e => e.Rank).ThenBy(e => e.Id).Select(e => (Guid?)e.Id).LastOrDefault();
        }

        return await rest.SendAsync<JsonNode>(HttpMethod.Put, $"api/epics/{epicId}/group",
            new { groupId, precedingId, followingId, version = epic.Version }, ct);
    }
}
