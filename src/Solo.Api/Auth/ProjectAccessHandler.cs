using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Domain;

namespace Solo.Api.Auth;

public sealed class ProjectAccessRequirement(ProjectAccessLevel level) : IAuthorizationRequirement
{
    public ProjectAccessLevel Level { get; } = level;
}

/// <summary>
/// Resource-based authorization: resolves the project from the route (directly
/// or by walking up from board/epic/task/sub-task/comment ids) and checks the
/// caller's membership + role. The same check gates SignalR group joins via
/// <see cref="ResolveProjectIdAsync"/>.
/// </summary>
public sealed class ProjectAccessHandler(
    CurrentUserAccessor currentUser,
    SoloDbContext db)
    : AuthorizationHandler<ProjectAccessRequirement>
{
    /// <summary>HttpContext.Items key caching the resolved project id per request.</summary>
    public const string ProjectIdItem = "Solo:ProjectId";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProjectAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext || currentUser.User is null)
        {
            return;
        }

        var projectId = await ResolveProjectIdFromRouteAsync(httpContext);
        if (projectId is null)
        {
            return;
        }

        httpContext.Items[ProjectIdItem] = projectId.Value;

        if (await AllowsAsync(db, currentUser.User.Id, currentUser.User.IsSuperAdmin,
                projectId.Value, requirement.Level, httpContext.RequestAborted))
        {
            context.Succeed(requirement);
        }
    }

    public static async Task<bool> AllowsAsync(
        SoloDbContext db, Guid userId, bool isSuperAdmin, Guid projectId,
        ProjectAccessLevel level, CancellationToken ct)
    {
        var membership = await db.ProjectMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        return AccessPolicy.Allows(membership?.Role, membership?.IsOwner ?? false, isSuperAdmin, level);
    }

    private async Task<Guid?> ResolveProjectIdFromRouteAsync(HttpContext httpContext)
    {
        var route = httpContext.GetRouteData().Values;
        var ct = httpContext.RequestAborted;

        if (TryGetGuid(route, "projectId", out var projectId))
        {
            return await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct)
                ? projectId
                : null;
        }

        if (TryGetGuid(route, "boardId", out var boardId))
        {
            return await ResolveProjectIdAsync(db, EntityRouteKind.Board, boardId, ct);
        }

        if (TryGetGuid(route, "epicId", out var epicId))
        {
            return await ResolveProjectIdAsync(db, EntityRouteKind.Epic, epicId, ct);
        }

        if (TryGetGuid(route, "taskId", out var taskId))
        {
            return await ResolveProjectIdAsync(db, EntityRouteKind.Task, taskId, ct);
        }

        if (TryGetGuid(route, "subTaskId", out var subTaskId))
        {
            return await ResolveProjectIdAsync(db, EntityRouteKind.SubTask, subTaskId, ct);
        }

        if (TryGetGuid(route, "commentId", out var commentId))
        {
            return await ResolveProjectIdAsync(db, EntityRouteKind.Comment, commentId, ct);
        }

        return null;
    }

    public enum EntityRouteKind
    {
        Board,
        Epic,
        Task,
        SubTask,
        Comment,
    }

    public static Task<Guid?> ResolveProjectIdAsync(
        SoloDbContext db, EntityRouteKind kind, Guid id, CancellationToken ct) => kind switch
    {
        EntityRouteKind.Board => FirstOrNull(
            db.Boards.Where(b => b.Id == id).Select(b => b.ProjectId), ct),
        EntityRouteKind.Epic => FirstOrNull(
            db.Epics.Where(e => e.Id == id).Select(e => e.Board!.ProjectId), ct),
        EntityRouteKind.Task => FirstOrNull(
            db.Tasks.Where(t => t.Id == id).Select(t => t.Board!.ProjectId), ct),
        EntityRouteKind.SubTask => FirstOrNull(
            db.SubTasks.Where(s => s.Id == id).Select(s => s.Task!.Board!.ProjectId), ct),
        EntityRouteKind.Comment => FirstOrNull(
            db.Comments.Where(c => c.Id == id)
                .Select(c => c.EpicId != null ? c.Epic!.Board!.ProjectId : c.Task!.Board!.ProjectId), ct),
        _ => Task.FromResult<Guid?>(null),
    };

    private static async Task<Guid?> FirstOrNull(IQueryable<Guid> query, CancellationToken ct)
    {
        var results = await query.Take(1).ToListAsync(ct);
        return results.Count == 0 ? null : results[0];
    }

    private static bool TryGetGuid(RouteValueDictionary route, string key, out Guid value)
    {
        value = default;
        return route.TryGetValue(key, out var raw) &&
               raw is not null &&
               Guid.TryParse(raw.ToString(), out value);
    }
}

public sealed class SuperAdminRequirement : IAuthorizationRequirement;

public sealed class SuperAdminHandler(CurrentUserAccessor currentUser)
    : AuthorizationHandler<SuperAdminRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SuperAdminRequirement requirement)
    {
        if (currentUser.User?.IsSuperAdmin == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
