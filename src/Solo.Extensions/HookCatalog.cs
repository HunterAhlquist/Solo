using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>One registered hook, resolved with the extension it came from.</summary>
public sealed record HookEntry(string ExtensionId, ISoloOperationHook Hook, int Order, bool FailOpen);

/// <summary>
/// Resolves the hooks that apply to an operation, in the order they run.
/// </summary>
/// <remarks>
/// <para>Ordering is <c>Order</c> ascending, ties broken by extension id, so
/// behaviour is identical across restarts rather than dependent on DI
/// registration order or filesystem enumeration.</para>
///
/// <para>A hook is dropped unless its extension declared the operation in the
/// manifest's <c>hooks</c> list. That is what makes the admin page's "these
/// extensions sit on this write path" trustworthy: a hook cannot quietly attach
/// to an operation its package never announced.</para>
/// </remarks>
public sealed class HookCatalog
{
    private readonly ConcurrentDictionary<Type, string?> _ownerCache = new();

    public IReadOnlyList<HookEntry> For(IServiceProvider services, string operation, ExtensionState state)
    {
        var hooks = services.GetServices<ISoloOperationHook>().ToList();
        if (hooks.Count == 0)
        {
            return [];
        }

        var entries = new List<HookEntry>();
        foreach (var hook in hooks)
        {
            if (!hook.Operations.Contains(operation))
            {
                continue;
            }

            var extensionId = OwnerOf(hook.GetType(), state);
            if (extensionId is null || state.IsDisabled(extensionId, out _))
            {
                continue;
            }

            var manifest = state.Find(extensionId)?.Manifest;
            if (manifest is null || !manifest.Hooks.Contains(operation))
            {
                // Registered a hook for an operation the manifest never declared.
                continue;
            }

            entries.Add(new HookEntry(extensionId, hook, hook.Order, manifest.FailOpen));
        }

        return entries
            .OrderBy(e => e.Order)
            .ThenBy(e => e.ExtensionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Which extension a hook type came from, by the assembly it lives in.</summary>
    private string? OwnerOf(Type hookType, ExtensionState state) =>
        _ownerCache.GetOrAdd(hookType, type =>
        {
            var assembly = type.Assembly;
            return state.Packages
                .FirstOrDefault(p => ReferenceEquals(p.LoadedAssembly, assembly))?.Id;
        });
}

/// <summary>
/// Supplies the identity and project access of the current caller.
/// </summary>
/// <remarks>
/// Implemented in Solo.Api, which owns <c>CurrentUserAccessor</c> and the
/// project-id that <c>ProjectAccessHandler</c> resolved for this request. The
/// host half of the extension system cannot reference Solo.Api — the dependency
/// runs the other way — so this is the seam between them.
/// </remarks>
public interface IExtensionCallerAccessor
{
    ValueTask<(ExtensionCaller Caller, Guid? ProjectId)> CurrentAsync(
        HttpContext http, CancellationToken ct);
}

/// <summary>Builds the target a hook is told about, from the route.</summary>
public static class HookContextFactory
{
    public static OperationTarget Target(ActionExecutingContext context, Guid? projectId)
    {
        var route = context.RouteData.Values;
        return new OperationTarget(
            ProjectId: projectId,
            BoardId: RouteGuid(route, "boardId"),
            EpicId: RouteGuid(route, "epicId"),
            TaskId: RouteGuid(route, "taskId"),
            SubTaskId: RouteGuid(route, "subTaskId"));
    }

    private static Guid? RouteGuid(RouteValueDictionary route, string key) =>
        route.TryGetValue(key, out var raw) && Guid.TryParse(raw?.ToString(), out var id) ? id : null;
}
