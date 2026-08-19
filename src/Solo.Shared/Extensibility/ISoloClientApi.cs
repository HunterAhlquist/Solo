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
    /// An absolute URL against the instance, for something the browser fetches
    /// itself — a stylesheet from the package's <c>content/</c>, an image — or
    /// for showing a user where their instance lives.
    /// </summary>
    Uri Resolve(string relativePath);

    /// <summary>GET, deserialized. Throws <see cref="SoloApiException"/> on a non-success status.</summary>
    Task<T> GetAsync<T>(string path, CancellationToken ct = default);

    /// <summary>A call with a response body. <paramref name="body"/> is serialized as JSON when present.</summary>
    Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default);

    /// <summary>A call whose response body is discarded — a 204, typically.</summary>
    Task SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default);
}

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
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Title { get; } = title;

    public string? Detail { get; } = detail;
}
