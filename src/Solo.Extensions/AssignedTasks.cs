using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared;
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
/// from one could not be moved anyway. Finished work stays for
/// <see cref="FinishedKeptFor"/> and then drops off, so the page is about what is
/// on someone's plate rather than everything they ever closed.
/// </remarks>
public static class AssignedTasks
{
    public static readonly AssignedTasksDto Empty = new([], [], [], [], [], [], []);

    public static readonly TimeSpan FinishedKeptFor = TimeSpan.FromDays(12);

    /// <summary>The tasks both queries start from: the person's, on open boards of projects they're still in.</summary>
    private static IQueryable<Solo.Data.Entities.TaskItem> Of(SoloDbContext db, Guid userId)
    {
        var finishedSince = DateTime.UtcNow - FinishedKeptFor;
        return db.Tasks.AsNoTracking()
            .Where(t => t.Board!.IsOpen &&
                        t.Assignments.Any(a => a.UserId == userId) &&
                        t.Board.Project!.Members.Any(m => m.UserId == userId) &&
                        (t.Status != TaskState.Finished || t.FinishedAt >= finishedSince));
    }

    /// <summary>
    /// The same work counted and listed for a glance (<c>GET api/users/me/work-summary</c>,
    /// the iPhone and iPad widgets): how many are in each column as the Your
    /// Tasks page shows them, and the open tasks by deadline.
    /// </summary>
    public static async Task<WorkSummaryDto> SummaryAsync(SoloDbContext db, Guid userId, CancellationToken ct)
    {
        var counts = await Of(db, userId)
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Count(TaskState status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        var open = await Of(db, userId)
            .Where(t => t.Status != TaskState.Finished)
            .OrderBy(t => t.DeadlineAt == null)
            .ThenBy(t => t.DeadlineAt)
            .ThenBy(t => t.Rank)
            .Take(WorkSummaryLimits.OpenListed)
            .Select(t => new
            {
                t.Id, t.Name, t.Number, t.Status, t.DeadlineAt,
                BoardAlias = t.Board!.Alias, ProjectAlias = t.Board.Project!.Alias,
            })
            .ToListAsync(ct);
        return new WorkSummaryDto(
            Count(TaskState.Unstarted),
            Count(TaskState.InProgress),
            Math.Min(Count(TaskState.Finished), WorkSummaryLimits.FinishedShown),
            open.Select(t => new WorkSummaryTaskDto(
                t.Id, Aliases.Serial(t.ProjectAlias, t.BoardAlias, t.Number), t.Name, t.Status, t.DeadlineAt,
                $"projects/{t.ProjectAlias}/boards/{t.BoardAlias}?task={t.Number}")).ToList());
    }

    public static async Task<AssignedTasksDto> QueryAsync(SoloDbContext db, Guid userId, CancellationToken ct)
    {
        var tasks = await Of(db, userId)
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

        // What a card needs to say where it lives on its board: its epic, and
        // that epic's group when it has one.
        var epicIds = tasks.Where(t => t.EpicId != null).Select(t => t.EpicId!.Value).Distinct().ToList();
        var epics = await db.Epics.AsNoTracking()
            .Where(e => epicIds.Contains(e.Id))
            .Include(e => e.Assignments)
            .ToListAsync(ct);
        var groupIds = epics.Where(e => e.GroupId != null).Select(e => e.GroupId!.Value).Distinct().ToList();
        var groups = await db.EpicGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id))
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
                t.StartAt)).ToList(),
            epics.Select(e => new EpicDto(
                e.Id, e.BoardId, e.Name, e.Description, e.Rank, e.Version,
                e.Assignments.Select(a => a.UserId).ToList(), e.GroupId)).ToList(),
            groups.Select(g => new EpicGroupDto(g.Id, g.BoardId, g.Name, g.Rank, g.Version)).ToList());
    }
}
