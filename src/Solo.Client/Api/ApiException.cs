using System.Net;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

/// <summary>
/// A non-success response from the Solo API, carrying its ProblemDetails.
/// </summary>
/// <remarks>
/// Derives from the contract's <see cref="SoloApiException"/> so an extension
/// page — which cannot reference this assembly — catches the same failures the
/// app does, from one hierarchy rather than two describing the same thing.
/// </remarks>
public class ApiException(HttpStatusCode statusCode, string? title, string? detail)
    : SoloApiException(statusCode, title, detail);

/// <summary>
/// A 409 from an optimistic-concurrency check: someone else saved first. The
/// caller re-fetches, replaces local state, and tells the user their view was
/// refreshed.
/// </summary>
public sealed class ApiConflictException(string? title, string? detail)
    : ApiException(HttpStatusCode.Conflict, title, detail);
