using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Solo.Mcp.Rest;

/// <summary>
/// Calls Solo's own REST API, over the loopback interface, carrying the MCP
/// token the request arrived with.
/// </summary>
/// <remarks>
/// <para><b>Why an HTTP hop rather than touching the database.</b> Every core
/// write path is a controller action wrapped in things that are not the action:
/// the project authorization policies, the <c>[SoloOperation]</c> hook filter
/// that lets another extension veto a move, optimistic-concurrency handling, the
/// audit interceptor, and the SignalR broadcast that repaints open boards.
/// Reaching around the pipeline would mean re-implementing all of it, and an MCP
/// write that quietly skipped another extension's hook would be a hole nobody
/// would find until it mattered. Re-entering the pipeline is correct by
/// construction: an MCP tool call is exactly as authorized, as audited and as
/// observable as the same action from a browser.</para>
///
/// <para>It is also what keeps this package free of core internals. The contract
/// offers <c>ISoloQuery</c> for reads and nothing for writes, and inventing a
/// private write path against <c>SoloDbContext</c> is precisely what the
/// contract exists to prevent.</para>
///
/// <para>The token is forwarded verbatim, so the second hop authenticates as the
/// same principal and lands in the same usage bucket. A token revoked
/// mid-conversation stops working on the next call, which is the point.</para>
/// </remarks>
public sealed class SoloRestClient(
    IHttpClientFactory factory,
    IHttpContextAccessor accessor,
    IServer server,
    IConfiguration configuration)
{
    public const string HttpClientName = "solo-mcp";

    /// <summary>
    /// Explicit base URL for the loopback hop. Set it when the app's listen
    /// address is not reachable as-is — and in tests, where there is no socket.
    /// </summary>
    public const string BaseUrlSetting = "Mcp:ApiBaseUrl";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Core serializes enums as strings (TaskState, LabelKind); read them back
        // the same way or every task round-trip loses its status.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The API's response as-is: a read tool's result is the REST payload verbatim.</summary>
    public Task<JsonNode?> GetAsync(string path, CancellationToken ct) =>
        SendAsync<JsonNode>(HttpMethod.Get, path, null, ct);

    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        where T : class
    {
        var http = accessor.HttpContext
            ?? throw new McpToolException("No HTTP context; the MCP endpoint is request-scoped.");

        using var request = new HttpRequestMessage(method, new Uri(BaseAddress(http), path.TrimStart('/')));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        // Verbatim: the second hop is the same caller, authenticated the same way.
        var authorization = http.Request.Headers.Authorization.ToString();
        if (authorization.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var client = factory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpToolException(
                $"Solo answered {(int)response.StatusCode} for {method} {path}" +
                await ProblemDetailAsync(response, ct));
        }

        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    /// <summary>
    /// Where to send the second hop.
    /// </summary>
    /// <remarks>
    /// The configured override first, then the address the server is actually
    /// listening on with its wildcard host resolved to loopback — that keeps the
    /// call inside the container and off the reverse proxy, which would otherwise
    /// have to route back to the app it just forwarded from. The incoming
    /// request's own origin is the last resort: the only option that always
    /// resolves, and the only one that leaves the box.
    /// </remarks>
    private Uri BaseAddress(HttpContext http)
    {
        var configured = configuration[BaseUrlSetting];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(WithTrailingSlash(configured), UriKind.Absolute);
        }

        // Prefer http: a loopback hop to an https listener would have to trust a
        // certificate issued for a name we are deliberately not using.
        var listen = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .OrderBy(a => a.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (listen is not null && Uri.TryCreate(Loopback(listen), UriKind.Absolute, out var address))
        {
            return address;
        }

        return new Uri($"{http.Request.Scheme}://{http.Request.Host}/", UriKind.Absolute);
    }

    /// <summary>Rewrites a wildcard listen address into one that can be dialled.</summary>
    private static string Loopback(string address) => WithTrailingSlash(
        address.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://*", "://127.0.0.1", StringComparison.Ordinal));

    private static string WithTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";

    /// <summary>ASP.NET problem+json carries the human-readable reason; surface it to the model.</summary>
    private static async Task<string> ProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return string.Empty;
        }

        try
        {
            var problem = JsonNode.Parse(text);
            var detail = string.Join(" — ", new[]
            {
                problem?["title"]?.GetValue<string>(),
                problem?["detail"]?.GetValue<string>(),
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (detail.Length > 0)
            {
                return $": {detail}";
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // Not problem+json — fall through to the raw body.
        }

        return text.Length > 0 ? $": {text}" : string.Empty;
    }
}

/// <summary>
/// A tool failure the model should read and act on — a 404, a validation
/// problem, a bad serial number. Surfaced as an <c>isError</c> tool result
/// rather than a JSON-RPC error, so the conversation can recover.
/// </summary>
public sealed class McpToolException(string message) : Exception(message);
