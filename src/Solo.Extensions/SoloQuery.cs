using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Domain;
using Solo.Data.Entities;
using Solo.Extensibility;
using Solo.Shared.Dtos;

namespace Solo.Extensions;

/// <summary>
/// Implements caller-scoped core reads for extensions.
/// </summary>
/// <remarks>
/// <para>Extensions cannot access <c>SoloDbContext</c>. Each method applies core
/// authorization before returning data.</para>
///
/// <para>Projections stay local because the extension host cannot reference
/// Solo.Api. Project lists match <c>GET /api/projects</c> and require membership;
/// entity reads also apply the super-admin path from <see cref="AccessPolicy"/>.</para>
///
/// <para>The caller is the request's, unless background work set one on
/// <see cref="ExtensionCallerOverride"/> for its scope. The override wins even
/// inside a request: <see cref="IHttpContextAccessor"/> flows into anything the
/// request awaits, so work started from an endpoint would otherwise read as
/// whoever made that request rather than the user it runs as.</para>
/// </remarks>
public sealed class SoloQuery(
    SoloDbContext db,
    IExtensionCallerAccessor caller,
    IHttpContextAccessor http,
    ExtensionCallerOverride background)
    : ISoloQuery
{
    public async Task<IReadOnlyList<ProjectSummaryDto>> ProjectsAsync(CancellationToken ct = default)
    {
        var user = await CallerAsync(ct);
        if (user.UserId == Guid.Empty)
        {
            return [];
        }

        return await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == user.UserId)
            .OrderBy(m => m.Project!.Name)
            .Select(m => new ProjectSummaryDto(
                m.ProjectId, m.Project!.Name, m.Project.Alias, m.Project.Icon, m.Project.ImageId,
                m.Role, m.IsOwner))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BoardDto>> BoardsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await AllowsAsync(projectId, ProjectAccessLevel.Read, ct))
        {
            return [];
        }

        return await db.Boards.AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderBy(b => b.Name)
            .Select(b => new BoardDto(b.Id, b.ProjectId, b.Name, b.Alias, b.Icon, b.ImageId, b.IsOpen, b.Version))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EpicDto>> EpicsAsync(Guid boardId, CancellationToken ct = default)
    {
        if (!await CanAccessBoardAsync(boardId, ProjectAccessLevel.Read, ct))
        {
            return [];
        }

        var epics = await db.Epics.AsNoTracking()
            .Where(e => e.BoardId == boardId)
            .Include(e => e.Assignments)
            .OrderBy(e => e.Rank)
            .ToListAsync(ct);

        return epics.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<TaskDto>> TasksAsync(Guid boardId, CancellationToken ct = default)
    {
        if (!await CanAccessBoardAsync(boardId, ProjectAccessLevel.Read, ct))
        {
            return [];
        }

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.BoardId == boardId)
            .Include(t => t.SubTasks)
            .Include(t => t.Assignments)
            .AsSplitQuery()
            .OrderBy(t => t.Rank)
            .ToListAsync(ct);

        return tasks.Select(ToDto).ToList();
    }

    public async Task<TaskDto?> TaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks.AsNoTracking()
            .Include(t => t.SubTasks)
            .Include(t => t.Assignments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);

        return task is not null && await CanAccessBoardAsync(task.BoardId, ProjectAccessLevel.Read, ct)
            ? ToDto(task)
            : null;
    }

    public async Task<IReadOnlyList<AssigneeIdentity>> TaskAssigneeIdentitiesAsync(
        Guid taskId, CancellationToken ct = default)
    {
        var task = await TaskAsync(taskId, ct);
        if (task is null) return [];
        return await db.Users.AsNoTracking().Where(u => task.AssigneeIds.Contains(u.Id))
            .Select(u => new AssigneeIdentity(u.Id, u.Issuer, u.Subject)).ToListAsync(ct);
    }

    public Task<bool> CanWriteBoardAsync(Guid boardId, CancellationToken ct = default) =>
        CanAccessBoardAsync(boardId, ProjectAccessLevel.Write, ct);

    public async Task<IReadOnlyList<ProjectMemberDto>> MembersAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await AllowsAsync(projectId, ProjectAccessLevel.Read, ct))
        {
            return [];
        }

        // Matches GET api/projects/{id}/members, which any reader already sees.
        return await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.Email)
            .Select(m => new ProjectMemberDto(
                m.Id, m.UserId, m.Email, m.User!.DisplayName, m.User.AvatarUrl,
                m.Role, m.IsOwner, m.UserId == null))
            .ToListAsync(ct);
    }

    public async Task<CommentDto?> CommentAsync(Guid commentId, CancellationToken ct = default)
    {
        var comment = await db.Comments.AsNoTracking()
            .Include(c => c.Author)
            .FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment is null)
        {
            return null;
        }

        // A comment is on exactly one of a task or an epic; its board decides access.
        var boardId = comment.TaskId is { } taskId
            ? await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => (Guid?)t.BoardId).FirstOrDefaultAsync(ct)
            : await db.Epics.AsNoTracking().Where(e => e.Id == comment.EpicId).Select(e => (Guid?)e.BoardId).FirstOrDefaultAsync(ct);

        return boardId is not null && await CanAccessBoardAsync(boardId.Value, ProjectAccessLevel.Read, ct)
            ? ToDto(comment)
            : null;
    }

    public async Task<IReadOnlyList<CommentDto>> TaskCommentsAsync(Guid taskId, CancellationToken ct = default)
    {
        var boardId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => (Guid?)t.BoardId)
            .FirstOrDefaultAsync(ct);
        if (boardId is null || !await CanAccessBoardAsync(boardId.Value, ProjectAccessLevel.Read, ct))
        {
            return [];
        }

        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.TaskId == taskId)
            .Include(c => c.Author)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return comments.Select(ToDto).ToList();
    }

    private async Task<bool> CanAccessBoardAsync(Guid boardId, ProjectAccessLevel level, CancellationToken ct)
    {
        var projectId = await db.Boards.AsNoTracking()
            .Where(b => b.Id == boardId)
            .Select(b => (Guid?)b.ProjectId)
            .FirstOrDefaultAsync(ct);
        return projectId is not null && await AllowsAsync(projectId.Value, level, ct);
    }

    private async Task<bool> AllowsAsync(Guid projectId, ProjectAccessLevel level, CancellationToken ct)
    {
        var user = await CallerAsync(ct);
        if (user.UserId == Guid.Empty)
        {
            return false;
        }

        var membership = await db.ProjectMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == user.UserId, ct);
        return AccessPolicy.Allows(membership?.Role, membership?.IsOwner ?? false, user.IsSuperAdmin, level);
    }

    public async Task<ExtensionCaller> CallerAsync(CancellationToken ct = default)
    {
        if (background.Caller is { } runAs)
        {
            return runAs;
        }

        var context = http.HttpContext;
        if (context is null)
        {
            return new ExtensionCaller(Guid.Empty, null, null, false, null, false);
        }

        var (current, _) = await caller.CurrentAsync(context, ct);
        return current;
    }

    private static EpicDto ToDto(Epic e) =>
        new(e.Id, e.BoardId, e.Name, e.Description, e.Rank, e.Version,
            e.Assignments.Select(a => a.UserId).ToList());

    private static TaskDto ToDto(TaskItem t) =>
        new(t.Id, t.BoardId, t.EpicId, t.Number, t.Name, t.LabelId, t.Description, t.Status,
            t.CreatedAt, t.DeadlineAt, t.Rank, t.Version,
            t.Assignments.Select(a => a.UserId).ToList(),
            t.SubTasks.OrderBy(s => s.Rank).Select(ToDto).ToList(),
            t.StartAt);

    private static CommentDto ToDto(Comment c) =>
        new(c.Id, c.AuthorId, c.Author?.DisplayName, c.Author?.AvatarUrl, c.Body, c.CreatedAt,
            c.EpicId, c.TaskId, c.Version);

    private static SubTaskDto ToDto(SubTask s) =>
        new(s.Id, s.TaskId, s.Name, s.LabelId, s.IsFinished, s.Rank, s.Version);
}
