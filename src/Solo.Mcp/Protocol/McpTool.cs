using System.Text.Json;
using System.Text.Json.Nodes;
using Solo.Mcp.Rest;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Mcp.Protocol;

/// <summary>One MCP tool: what a client sees in <c>tools/list</c>, and what runs on <c>tools/call</c>.</summary>
public sealed record McpTool(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<JsonNode?>> Handler);
