using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Domain;
using Solo.Data.Entities;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Api.Controllers;

public sealed class CommentsController(
    SoloDbContext db,
    CurrentUserAccessor currentUser,
    IBoardNotifier notifier)
    : ApiControllerBase
{
    [HttpGet("api/epics/{epicId:guid}/comments")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public Task<ActionResult<IReadOnlyList<CommentDto>>> ListForEpic(Guid epicId, CancellationToken ct) =>
        List(c => c.EpicId == epicId, ct);

    [HttpGet("api/tasks/{taskId:guid}/comments")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public Task<ActionResult<IReadOnlyList<CommentDto>>> ListForTask(Guid taskId, CancellationToken ct) =>
        List(c => c.TaskId == taskId, ct);

    [HttpPost("api/epics/{epicId:guid}/comments")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<CommentDto>> AddToEpic(
        Guid epicId, CreateCommentRequest request, CancellationToken ct)
    {
        var boardId = await db.Epics.AsNoTracking()
            .Where(e => e.Id == epicId).Select(e => (Guid?)e.BoardId).FirstOrDefaultAsync(ct);
        if (boardId is null)
        {
            return NotFound();
        }

        return await Add(new Comment
        {
            AuthorId = currentUser.User!.Id,
            Body = request.Body,
            EpicId = epicId,
        }, boardId.Value, ct);
    }

    [HttpPost("api/tasks/{taskId:guid}/comments")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<CommentDto>> AddToTask(
        Guid taskId, CreateCommentRequest request, CancellationToken ct)
    {
        var boardId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId).Select(t => (Guid?)t.BoardId).FirstOrDefaultAsync(ct);
        if (boardId is null)
        {
            return NotFound();
        }

        return await Add(new Comment
        {
            AuthorId = currentUser.User!.Id,
            Body = request.Body,
            TaskId = taskId,
        }, boardId.Value, ct);
    }

    /// <summary>Authors edit their own comments.</summary>
    [HttpPut("api/comments/{commentId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<CommentDto>> Update(
        Guid commentId, UpdateCommentRequest request, CancellationToken ct)
    {
        var comment = await db.Comments.Include(c => c.Author)
            .FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment is null)
        {
            return NotFound();
        }

        if (comment.AuthorId != currentUser.User!.Id)
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not the author",
                detail: "Only the author can edit a comment.");
        }

        comment.Body = request.Body;
        UseClientVersion(db, comment, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await Notify(comment, ChangeKind.Updated, ct, nameof(comment.Body));
        return comment.ToDto();
    }

    /// <summary>Authors delete their own comments; project owners can moderate any.</summary>
    [HttpDelete("api/comments/{commentId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid commentId, CancellationToken ct)
    {
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment is null)
        {
            return NotFound();
        }

        if (comment.AuthorId != currentUser.User!.Id)
        {
            var projectId = (Guid)HttpContext.Items[ProjectAccessHandler.ProjectIdItem]!;
            var isOwner = await ProjectAccessHandler.AllowsAsync(
                db, currentUser.User.Id, currentUser.User.IsSuperAdmin, projectId,
                ProjectAccessLevel.Owner, ct);
            if (!isOwner)
            {
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Not the author",
                    detail: "Only the author or a project owner can delete a comment.");
            }
        }

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);
        await Notify(comment, ChangeKind.Deleted, ct);
        return NoContent();
    }

    private async Task<ActionResult<CommentDto>> Add(Comment comment, Guid boardId, CancellationToken ct)
    {
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Comment, comment.Id, ChangeKind.Created);
        comment.Author = currentUser.User;
        return comment.ToDto();
    }

    private async Task<ActionResult<IReadOnlyList<CommentDto>>> List(
        System.Linq.Expressions.Expression<Func<Comment, bool>> filter, CancellationToken ct)
    {
        var comments = await db.Comments.AsNoTracking()
            .Where(filter)
            .Include(c => c.Author)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return comments.Select(c => c.ToDto()).ToList();
    }

    private async Task Notify(Comment comment, ChangeKind kind, CancellationToken ct, params string[] fields)
    {
        var boardId = comment.EpicId is not null
            ? await db.Epics.AsNoTracking()
                .Where(e => e.Id == comment.EpicId).Select(e => (Guid?)e.BoardId).FirstOrDefaultAsync(ct)
            : await db.Tasks.AsNoTracking()
                .Where(t => t.Id == comment.TaskId).Select(t => (Guid?)t.BoardId).FirstOrDefaultAsync(ct);
        if (boardId is not null)
        {
            await notifier.BoardEntityChanged(boardId.Value, EntityTypes.Comment, comment.Id, kind, fields);
        }
    }
}
