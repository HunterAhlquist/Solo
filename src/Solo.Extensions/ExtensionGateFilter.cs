using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Solo.Extensions;

/// <summary>
/// Answers 503 for a request to an extension that is loaded but disabled.
/// </summary>
/// <remarks>
/// This is the request-time half of the ordering trap: services are registered
/// before <c>builder.Build()</c> and migrations run after it, so an extension
/// whose migration failed already has its routes mapped. Without this gate it
/// would serve requests against tables that do not exist, and the operator would
/// see an EF error instead of a sentence naming the extension.
/// </remarks>
public sealed class ExtensionGateFilter(ExtensionState state) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var extensionId = ExtensionRouting.IdFromRoute(context.HttpContext.Request.Path);
        if (extensionId is not null && state.IsDisabled(extensionId, out var reason))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Extension unavailable",
                Detail = $"The '{extensionId}' extension is disabled on this instance: {reason}",
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        await next();
    }
}

/// <summary>Route helpers shared by the gate filter and the serving endpoints.</summary>
public static class ExtensionRouting
{
    public const string Prefix = "/api/ext/";

    /// <summary>The extension id in an <c>/api/ext/{id}/…</c> path, or null.</summary>
    public static string? IdFromRoute(PathString path)
    {
        var value = path.Value;
        if (value is null || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = value[Prefix.Length..];
        var slash = rest.IndexOf('/');
        var id = slash < 0 ? rest : rest[..slash];
        return id.Length == 0 ? null : id;
    }
}
