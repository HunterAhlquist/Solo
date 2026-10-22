using System.Net;
using System.Net.Http;

namespace Solo.Shared.Extensibility;

/// <summary>
/// The API an extension's client assembly is given: authenticated calls to the
/// instance, over the host's own HTTP path.
/// </summary>
/// <remarks>
/// <para>Extension pages run inside the host app, so MudBlazor, the router,
/// dialogs and JS interop all resolve from its service provider already. The one
/// thing an extension could not previously do is <em>call its own endpoints</em>:
/// the host registers <c>HttpClient</c> only as a typed client for its own API
/// classes, and there was nothing an extension could inject. This is that seam.</para>
///
/// <para>It lives in <c>Solo.Shared</c> rather than <c>Solo.Extensibility</c> for
/// the same reason <see cref="ContractVersion"/> does: the contract assembly
/// carries a FrameworkReference to ASP.NET Core and an EF Core dependency, and
/// neither resolves for <c>browser-wasm</c>.</para>
///
/// <para>The implementation is the host's own client, so an extension gets the
/// same credential, the same base address, and the same ProblemDetails handling
/// core uses — and nothing an extension does can bypass the authentication the
/// app is running under.</para>
/// </remarks>
public interface ISoloClientApi
{
    /// <summary>
    /// An absolute URL for something <em>this browser</em> fetches — a
    /// stylesheet from the package's <c>content/</c>, an image.
    /// </summary>
    Uri Resolve(string relativePath);

    /// <summary>
    /// An absolute URL for something <em>outside</em> the browser to reach: an
    /// address you are showing a user to paste into another tool.
    /// </summary>
    /// <remarks>
    /// Usually identical to <see cref="Resolve"/>, and different in exactly one
    /// place that matters — the desktop shell, which serves the client from a
    /// local origin and proxies the API onward. There, <see cref="Resolve"/>
    /// yields <c>http://localhost:41321/…</c>, which is correct for a fetch and
    /// useless in an external tool's config. Use this one for anything a person
    /// copies out of the app.
    /// </remarks>
    Uri ResolveExternal(string relativePath);

    /// <summary>GET, deserialized. Throws <see cref="SoloApiException"/> on a non-success status.</summary>
    Task<T> GetAsync<T>(string path, CancellationToken ct = default);

    /// <summary>A call with a response body. <paramref name="body"/> is serialized as JSON when present.</summary>
    Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default);

    /// <summary>GET as bytes, using the same credential and error handling as JSON calls (contract 1.10).</summary>
    Task<SoloBinary> GetBytesAsync(string path, CancellationToken ct = default);

    /// <summary>Sends raw bytes and reads a JSON response (contract 1.10).</summary>
    Task<T> SendBytesAsync<T>(HttpMethod method, string path, byte[] content, string contentType,
        CancellationToken ct = default);

    /// <summary>A call whose response body is discarded — a 204, typically.</summary>
    Task SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default);
}

public sealed record SoloBinary(byte[] Content, string? ContentType);

/// <summary>
/// A non-success response from the Solo API, carrying its ProblemDetails.
/// </summary>
/// <remarks>
/// The type an extension page catches. The host's own <c>ApiException</c>
/// derives from this, so there is one exception hierarchy rather than two
/// describing the same thing.
/// </remarks>
public class SoloApiException(HttpStatusCode statusCode, string? title, string? detail)
    : Exception(detail ?? title ?? $"The request failed with status {(int)statusCode}.")
{
    /// <summary>The server's Retry-After delay, when supplied (contract 1.10).</summary>
    public TimeSpan? RetryAfter { get; init; }

    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Title { get; } = title;

    public string? Detail { get; } = detail;
}
