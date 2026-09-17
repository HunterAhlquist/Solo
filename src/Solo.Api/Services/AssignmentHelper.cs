using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Services;

public static class AssignmentHelper
{
    /// <summary>Assignees must be activated members of the project.</summary>
    public static async Task<IReadOnlyList<Guid>> InvalidAssignees(
        SoloDbContext db, Guid projectId, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var distinct = userIds.Distinct().ToList();
        var memberUserIds = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId != null && distinct.Contains(m.UserId.Value))
            .Select(m => m.UserId!.Value)
            .ToListAsync(ct);
        return distinct.Except(memberUserIds).ToList();
    }

    public static ObjectResult InvalidAssigneesProblem(ControllerBase controller, IReadOnlyList<Guid> invalid) =>
        controller.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid assignees",
            detail: $"Not project members: {string.Join(", ", invalid)}");

    /// <summary>Replaces the current assignments with the requested user ids.</summary>
    /// <remarks>
    /// <paramref name="current"/> must be the tracked principal's own navigation: it is
    /// both the list to reconcile and the list the caller then maps to a DTO. Stale rows
    /// must be deleted because removing them from the navigation only clears the optional FK.
    /// </remarks>
    public static void Sync(
        SoloDbContext db, List<Assignment> current, IReadOnlyList<Guid> userIds,
        Func<Guid, Assignment> create)
    {
        var wanted = userIds.ToHashSet();
        var stale = current.Where(a => !wanted.Contains(a.UserId)).ToList();
        if (stale.Count > 0)
        {
            db.Assignments.RemoveRange(stale);
            current.RemoveAll(a => !wanted.Contains(a.UserId));
        }

        var assigned = current.Select(a => a.UserId).ToHashSet();
        foreach (var userId in wanted)
        {
            if (!assigned.Add(userId))
            {
                continue;
            }

            // Tracking the row is all there is to do: `create` points it at a principal
            // this context already tracks, so relationship fixup appends it to that
            // principal's navigation — which is `current`. Appending it again here would
            // list the assignee twice in the DTO mapped from the navigation.
            db.Assignments.Add(create(userId));
        }
    }
}
