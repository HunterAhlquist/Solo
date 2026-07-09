using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Api.Controllers;

public sealed class BoardsController(SoloDbContext db, IBoardNotifier notifier) : ApiControllerBase
{
    [HttpGet("api/projects/{projectId:guid}/boards")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<BoardDto>>> List(Guid projectId, CancellationToken ct)
    {
        var boards = await db.Boards.AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        return boards.Select(b => b.ToDto()).ToList();
    }

    [HttpPost("api/projects/{projectId:guid}/boards")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<BoardDto>> Create(
        Guid projectId, CreateBoardRequest request, CancellationToken ct)
    {
        var board = new Board
        {
            ProjectId = projectId,
            Name = request.Name,
            Alias = Aliases.Normalize(request.Alias),
            Icon = request.Icon,
            ImageId = request.ImageId,
        };
        db.Boards.Add(board);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e, "IX_Boards_ProjectId_Alias"))
        {
            return AliasTakenProblem("board in this project");
        }

        await notifier.ProjectEntityChanged(projectId, EntityTypes.Board, board.Id, ChangeKind.Created);
        return CreatedAtAction(nameof(Get), new { boardId = board.Id }, board.ToDto());
    }

    /// <summary>Full board payload: epics, tasks (with sub-tasks + assignees), resolved labels.</summary>
    [HttpGet("api/boards/{boardId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<BoardDetailDto>> Get(Guid boardId, CancellationToken ct)
    {
        var board = await LoadDetail(b => b.Id == boardId, ct);
        return board is null ? NotFound() : await BuildDetailAsync(board, ct);
    }

    /// <summary>Alias lookup: what the client's aliased routes resolve through.</summary>
    [HttpGet("api/projects/by-alias/{projectAlias}/boards/{boardAlias}")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<BoardDetailDto>> GetByAlias(
        string projectAlias, string boardAlias, CancellationToken ct)
    {
        var project = Aliases.Normalize(projectAlias);
        var alias = Aliases.Normalize(boardAlias);
        var board = await LoadDetail(b => b.Alias == alias && b.Project!.Alias == project, ct);
        return board is null ? NotFound() : await BuildDetailAsync(board, ct);
    }

    private Task<Board?> LoadDetail(
        System.Linq.Expressions.Expression<Func<Board, bool>> filter, CancellationToken ct) =>
        db.Boards.AsNoTracking()
            .Include(b => b.Project)
            .Include(b => b.Epics).ThenInclude(e => e.Assignments)
            .Include(b => b.Tasks).ThenInclude(t => t.SubTasks)
            .Include(b => b.Tasks).ThenInclude(t => t.Assignments)
            .FirstOrDefaultAsync(filter, ct);

    private async Task<BoardDetailDto> BuildDetailAsync(Board board, CancellationToken ct)
    {
        var labels = await db.Labels.AsNoTracking()
            .Where(l => l.ProjectId == board.ProjectId && (l.BoardId == null || l.BoardId == board.Id))
            .OrderBy(l => l.Kind).ThenBy(l => l.Name)
            .ToListAsync(ct);

        return new BoardDetailDto(
            board.Id, board.ProjectId, board.Project!.Alias, board.Name, board.Alias,
            board.Icon, board.ImageId, board.IsOpen, board.Version,
            board.Epics.OrderBy(e => e.Rank).Select(e => e.ToDto()).ToList(),
            board.Tasks.OrderBy(t => t.Rank).Select(t => t.ToDto()).ToList(),
            labels.Select(l => l.ToDto()).ToList());
    }

    [HttpPut("api/boards/{boardId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<BoardDto>> Update(
        Guid boardId, UpdateBoardRequest request, CancellationToken ct)
    {
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        board.Name = request.Name;
        board.Alias = Aliases.Normalize(request.Alias);
        board.Icon = request.Icon;
        board.ImageId = request.ImageId;
        UseClientVersion(db, board, request.Version);
        try
        {
            var conflict = await SaveOrConflictAsync(db, ct);
            if (conflict is not null)
            {
                return conflict;
            }
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e, "IX_Boards_ProjectId_Alias"))
        {
            return AliasTakenProblem("board in this project");
        }

        await notifier.BoardEntityChanged(boardId, EntityTypes.Board, boardId,
            ChangeKind.Updated, nameof(board.Name), nameof(board.Alias),
            nameof(board.Icon), nameof(board.ImageId));
        return board.ToDto();
    }

    /// <summary>Open/Close status per concept.</summary>
    [HttpPut("api/boards/{boardId:guid}/open")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<BoardDto>> SetOpen(
        Guid boardId, SetBoardOpenRequest request, CancellationToken ct)
    {
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        board.IsOpen = request.IsOpen;
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Board, boardId,
            ChangeKind.Updated, nameof(board.IsOpen));
        return board.ToDto();
    }

    [HttpDelete("api/boards/{boardId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid boardId, CancellationToken ct)
    {
        var board = await db.Boards
            .Include(b => b.Epics)
            .Include(b => b.Tasks).ThenInclude(t => t.SubTasks)
            .FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        var projectId = board.ProjectId;
        db.Boards.Remove(board);
        await db.SaveChangesAsync(ct);
        await notifier.ProjectEntityChanged(projectId, EntityTypes.Board, boardId, ChangeKind.Deleted);
        return NoContent();
    }
}
