using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared.Dtos;

namespace Solo.Extensions;

/// <summary>
/// One person's work across the instance: the query behind both
/// <c>GET api/users/me/tasks</c> and <c>ISoloQuery.AssignedTasksAsync</c>, so
/// the dashboard and an extension reading it can never disagree on what counts.
/// </summary>
/// <remarks>
/// Membership is the filter, not the assignment alone: an assignment outlives
/// being removed from a project, and a person who was shown the door must not
/// keep seeing that project's tasks on their own page. Closed boards are left
/// out the way the sidebar leaves them out — their work is archived, and a card
/// from one could not be moved anyway.
/// </remarks>
public static class AssignedTasks
{
    public static readonly AssignedTasksDto Empty = new([], [], [], [], []);

    public static async Task<AssignedTasksDto> QueryAsync(SoloDbContext db, Guid userId, CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.Board!.IsOpen &&
                        t.Assignments.Any(a => a.UserId == userId) &&
                        t.Board.Project!.Members.Any(m => m.UserId == userId))
            .Include(t => t.SubTasks)
            .Include(t => t.Assignments)
            .AsSplitQuery()
            .OrderBy(t => t.Rank)
            .ToListAsync(ct);
        if (tasks.Count == 0)
        {
            return Empty;
        }

        var boardIds = tasks.Select(t => t.BoardId).Distinct().ToList();
        var boards = await db.Boards.AsNoTracking()
            .Where(b => boardIds.Contains(b.Id))
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        var projectIds = boards.Select(b => b.ProjectId).Distinct().ToList();

        // The caller's own memberships, which the filter above guarantees exist.
        var projects = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId && projectIds.Contains(m.ProjectId))
            .OrderBy(m => m.Project!.Name)
            .Select(m => new ProjectSummaryDto(
                m.ProjectId, m.Project!.Name, m.Project.Alias, m.Project.Icon, m.Project.ImageId,
                m.Role, m.IsOwner))
            .ToListAsync(ct);

        // Only what a card can wear: a task's own label is one of its board's.
        var labels = await db.Labels.AsNoTracking()
            .Where(l => l.BoardId != null && boardIds.Contains(l.BoardId.Value))
            .OrderBy(l => l.Name)
            .ToListAsync(ct);

        // One face per person. The same user is a member of several of these
        // projects under rows of their own; the first wins, and nothing here
        // reads the role off it.
        var members = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId != null && projectIds.Contains(m.ProjectId))
            .OrderBy(m => m.Email)
            .Select(m => new ProjectMemberDto(
                m.Id, m.UserId, m.Email, m.User!.DisplayName, m.User.AvatarUrl,
                m.Role, m.IsOwner, false))
            .ToListAsync(ct);

        return new AssignedTasksDto(
            projects,
            boards.Select(b => new BoardDto(b.Id, b.ProjectId, b.Name, b.Alias, b.Icon, b.ImageId, b.IsOpen, b.Version)).ToList(),
            labels.Select(l => new LabelDto(l.Id, l.ProjectId, l.BoardId, l.Kind, l.Name, l.Color, l.Icon, l.ImageId, l.Version)).ToList(),
            members.DistinctBy(m => m.UserId).ToList(),
            tasks.Select(t => new TaskDto(
                t.Id, t.BoardId, t.EpicId, t.Number, t.Name, t.LabelId, t.Description, t.Status,
                t.CreatedAt, t.DeadlineAt, t.Rank, t.Version,
                t.Assignments.Select(a => a.UserId).ToList(),
                t.SubTasks.OrderBy(s => s.Rank).Select(s => new SubTaskDto(
                    s.Id, s.TaskId, s.Name, s.LabelId, s.IsFinished, s.Rank, s.Version)).ToList(),
                t.StartAt)).ToList());
    }
}
