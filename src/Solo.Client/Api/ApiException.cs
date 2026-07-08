using System.Net;

namespace Solo.Client.Api;

/// <summary>A non-success response from the Solo API, carrying its ProblemDetails.</summary>
public class ApiException(HttpStatusCode statusCode, string? title, string? detail)
    : Exception(detail ?? title ?? $"The request failed with status {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Title { get; } = title;

    public string? Detail { get; } = detail;
}

/// <summary>
/// A 409 from an optimistic-concurrency check: someone else saved first. The
/// caller re-fetches, replaces local state, and tells the user their view was
/// refreshed.
/// </summary>
public sealed class ApiConflictException(string? title, string? detail)
    : ApiException(HttpStatusCode.Conflict, title, detail);
