using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solo.Mcp.Auth;
using Solo.Mcp.Protocol;

namespace Solo.Mcp.Controllers;

/// <summary>
/// The MCP endpoint, served at <c>/api/ext/mcp</c>.
/// </summary>
/// <remarks>
/// <para>Route-wise this is <c>[Route("")]</c>: the host rewrites every
/// extension controller under <c>/api/ext/{id}/</c>, so declaring the empty
/// template is what puts the endpoint at the extension root — the shortest URL a
/// user has to paste into an MCP client's config.</para>
///
/// <para>Access is deliberately narrower than <c>[Authorize]</c>: it takes an
/// MCP token specifically, not any signed-in principal. That is what makes the
/// MCP surface something a user grants, an admin audits and either can revoke —
/// a browser session reaching it would be usage nobody could see or cut off.</para>
/// </remarks>
[ApiController]
[Route("")]
[Authorize]
public sealed class McpController(McpServer server) : ControllerBase
{
    /// <summary>Cap on a single JSON-RPC message, mirroring the desktop server's.</summary>
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        if (RequireMcpToken() is { } refusal)
        {
            return refusal;
        }

        JsonNode? message;
        try
        {
            message = await ReadBodyAsync(ct);
        }
        catch (InvalidOperationException e)
        {
            return RpcError(StatusCodes.Status413PayloadTooLarge, -32600, e.Message);
        }
        catch (JsonException)
        {
            return RpcError(StatusCodes.Status400BadRequest, -32700, "Request body is not valid JSON.");
        }

        var response = await server.HandleAsync(message, ct);

        // A notification, or a batch made only of them: there is nothing to send
        // back, and 202 is what the transport asks for in that case.
        return response is null
            ? StatusCode(StatusCodes.Status202Accepted)
            : new ContentResult
            {
                Content = response.ToJsonString(),
                ContentType = "application/json; charset=utf-8",
                StatusCode = StatusCodes.Status200OK,
            };
    }

    /// <summary>
    /// The GET half of Streamable HTTP opens a server-push stream. This server
    /// is stateless and has nothing to push, so it says so rather than holding
    /// an idle connection open.
    /// </summary>
    [HttpGet]
    [HttpDelete]
    public IActionResult NoStream()
    {
        Response.Headers.Allow = "POST";
        return RpcError(
            StatusCodes.Status405MethodNotAllowed, -32000,
            "This MCP endpoint is stateless: POST only, no event stream.");
    }

    /// <summary>
    /// What the MCP access page shows: which tools this instance exposes and
    /// which protocol revision it speaks.
    /// </summary>
    /// <remarks>
    /// Plain <c>[Authorize]</c>, unlike the endpoint itself — this is read by a
    /// signed-in browser deciding what to render, and carries nothing an MCP
    /// token holder could not already get from <c>tools/list</c>. It doubles as
    /// how the client detects that this extension is installed at all.
    /// </remarks>
    [HttpGet("info")]
    public ActionResult<McpInfoDto> Info() => new McpInfoDto(
        McpServer.ProtocolVersion,
        McpServer.ServerVersion,
        server.Tools
            .Select(t => new McpToolInfoDto(t.Name, t.Title, t.Description))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList());

    /// <summary>
    /// Refuses anything that is not an MCP token — a browser session, or an OIDC
    /// ID token — with a message that says where to get one.
    /// </summary>
    private ObjectResult? RequireMcpToken() =>
        User.HasClaim(c => c.Type == McpAuthentication.TokenIdClaim)
            ? null
            : Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "An MCP token is required",
                detail: $"Authenticate with a Solo MCP token (Authorization: Bearer {McpTokens.Prefix}…). " +
                        "Generate one from MCP access in the Solo app.");

    /// <summary>
    /// Reads the body under a hard cap.
    /// </summary>
    /// <remarks>
    /// Bounded by copy rather than by Content-Length: a chunked request declares
    /// no length, so trusting the header would leave the cap trivially bypassed.
    /// </remarks>
    private async Task<JsonNode?> ReadBodyAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new InvalidOperationException(
                    $"The request body exceeds the {MaxBodyBytes / (1024 * 1024)} MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer.Length == 0 ? null : await JsonNode.ParseAsync(buffer, cancellationToken: ct);
    }

    private ContentResult RpcError(int status, int code, string message) => new()
    {
        Content = McpServer.Error(null, code, message).ToJsonString(),
        ContentType = "application/json; charset=utf-8",
        StatusCode = status,
    };
}

/// <summary>What the MCP access page needs in order to describe this endpoint.</summary>
public sealed record McpInfoDto(
    string ProtocolVersion,
    string ServerVersion,
    IReadOnlyList<McpToolInfoDto> Tools);

public sealed record McpToolInfoDto(string Name, string Title, string Description);
