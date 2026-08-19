using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Solo.Mcp.Rest;

namespace Solo.Mcp.Protocol;

/// <summary>
/// The JSON-RPC half of the MCP endpoint: one message in, one response out.
/// </summary>
/// <remarks>
/// <para><b>Stateless by design</b>, exactly as the desktop shell's local server
/// was. Every POST is answered on its own with plain JSON — no SSE stream, no
/// <c>Mcp-Session-Id</c>, no server-side session bookkeeping. MCP clients
/// re-send <c>initialize</c> whenever they need to, so the endpoint survives an
/// instance restart with nothing to reconcile, and GET — a server-push stream we
/// do not offer — can honestly answer 405.</para>
///
/// <para>Hand-rolled rather than built on the MCP C# SDK because an extension
/// only gets <c>ConfigureServices</c> and an application part: it cannot map
/// endpoints, which is how that SDK attaches its transport. The surface a
/// stateless server owes a client is small enough that the trade is worth it.</para>
/// </remarks>
public sealed class McpServer(McpToolCatalog catalog, ILogger<McpServer> logger)
{
    /// <summary>Protocol revisions understood here, newest first.</summary>
    private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string ProtocolVersion => SupportedProtocols[0];

    public static string ServerVersion =>
        typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(McpServer).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public IReadOnlyCollection<McpTool> Tools => catalog.Tools;

    /// <summary>
    /// Handles one message, or a batch of them. Null means "nothing to send
    /// back" — a notification, or a batch made entirely of notifications.
    /// </summary>
    public async Task<JsonNode?> HandleAsync(JsonNode? message, CancellationToken ct)
    {
        // Batches were dropped in 2025-06-18 but are still legal in the two
        // earlier revisions, and clients pinned to those send them.
        if (message is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch.ToArray())
            {
                if (await HandleAsync(item?.DeepClone(), ct) is { } response)
                {
                    responses.Add(response);
                }
            }

            return responses.Count > 0 ? responses : null;
        }

        if (message is not JsonObject request)
        {
            return Error(null, -32600, "A JSON-RPC message must be an object, or an array of objects.");
        }

        var id = request["id"];
        var method = request["method"]?.GetValue<string>();
        if (method is null)
        {
            return Error(id, -32600, "Missing \"method\".");
        }

        // No id means a notification: act on it, answer nothing. Replying to one
        // is a protocol violation, not a harmless extra.
        var isNotification = id is null;

        try
        {
            var result = await DispatchAsync(method, request["params"] as JsonObject, ct);
            if (isNotification)
            {
                return null;
            }

            return result is null
                ? Error(id, -32601, $"Unknown method \"{method}\".")
                : Success(id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "MCP method {Method} failed.", method);
            return isNotification ? null : Error(id, -32603, $"{method} failed: {e.Message}");
        }
    }

    /// <summary>Null result means the method is not one this server implements.</summary>
    private async Task<JsonNode?> DispatchAsync(string method, JsonObject? parameters, CancellationToken ct) =>
        method switch
        {
            "initialize" => Initialize(parameters),
            "ping" => new JsonObject(),
            "tools/list" => ToolList(),
            "tools/call" => await CallToolAsync(parameters, ct),
            // Lifecycle notifications: acknowledged by not failing.
            "notifications/initialized" or "notifications/cancelled" => new JsonObject(),
            _ => null,
        };

    private static JsonObject Initialize(JsonObject? parameters)
    {
        // Echo the client's revision when we speak it, so a client pinned to an
        // older one is not forced to downgrade the session.
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        var agreed = requested is not null && SupportedProtocols.Contains(requested, StringComparer.Ordinal)
            ? requested
            : ProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = agreed,
            ["capabilities"] = new JsonObject
            {
                // No listChanged: the catalog is fixed at build time, so
                // promising change notifications would be a promise never kept.
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "solo",
                ["title"] = "Solo",
                ["version"] = ServerVersion,
            },
            ["instructions"] = McpToolCatalog.Instructions,
        };
    }

    private JsonObject ToolList()
    {
        var tools = new JsonArray();
        foreach (var tool in catalog.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    /// <summary>
    /// Runs a tool. A failure here is a <em>result</em>, not a protocol error:
    /// the model has to be able to read "that board doesn't exist" and try
    /// something else, which a JSON-RPC error would deny it.
    /// </summary>
    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (name is null)
        {
            return ToolError("tools/call needs a \"name\".");
        }

        if (catalog.Find(name) is not { } tool)
        {
            return ToolError($"Unknown tool \"{name}\".");
        }

        var arguments = parameters?["arguments"] as JsonObject ?? [];
        try
        {
            var result = await tool.Handler(arguments.DeepClone().AsObject(), ct);
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result is null ? "ok" : result.ToJsonString(Indented),
                }),
            };
        }
        catch (McpToolException e)
        {
            return ToolError(e.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Unexpected: the model still gets something it can act on, but this
            // one belongs in the log with a stack trace.
            logger.LogError(e, "MCP tool {Tool} threw.", name);
            return ToolError($"{name} failed: {e.Message}");
        }
    }

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    private static JsonObject Success(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
