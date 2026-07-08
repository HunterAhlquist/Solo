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

    /// <summary>
    /// Reconciles the assignment collection to exactly the requested user ids.
    /// Rows are removed via the context: detaching them from the navigation
    /// alone would null the optional parent FK instead of deleting the row.
    /// </summary>
    public static void Sync(
        SoloDbContext db, List<Assignment> current, IReadOnlyList<Guid> userIds,
        Func<Guid, Assignment> create)
    {
        var wanted = userIds.Distinct().ToHashSet();
        foreach (var stale in current.Where(a => !wanted.Contains(a.UserId)).ToList())
        {
            db.Assignments.Remove(stale);
            current.Remove(stale);
        }

        foreach (var userId in wanted.Where(id => current.All(a => a.UserId != id)))
        {
            var assignment = create(userId);
            db.Assignments.Add(assignment);
            current.Add(assignment);
        }
    }
}
