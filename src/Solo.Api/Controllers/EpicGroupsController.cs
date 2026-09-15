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

public sealed class EpicGroupsController(SoloDbContext db, IBoardNotifier notifier) : ApiControllerBase
{
    [HttpGet("api/boards/{boardId:guid}/groups")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<EpicGroupDto>>> List(Guid boardId, CancellationToken ct) =>
        (await db.EpicGroups.AsNoTracking().Where(g => g.BoardId == boardId)
            .OrderBy(g => g.Rank).ToListAsync(ct)).Select(g => g.ToDto()).ToList();

    [HttpGet("api/boards/{boardId:guid}/groups/{groupId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<EpicGroupDto>> Get(Guid boardId, Guid groupId, CancellationToken ct)
    {
        var group = await db.EpicGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId && g.BoardId == boardId, ct);
        return group is null ? NotFound() : group.ToDto();
    }

    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    [HttpPost("api/boards/{boardId:guid}/groups")]
    public async Task<ActionResult<EpicGroupDto>> Create(Guid boardId, CreateEpicGroupRequest request, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return NotFound();
        var siblings = await EpicRanking.Siblings(db, boardId, null, ct);
        var group = new EpicGroup { BoardId = boardId, Name = request.Name, Rank = Ranking.ForAppend(siblings.Select(i => i.Rank)) };
        db.EpicGroups.Add(group);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.EpicGroup, group.Id, ChangeKind.Created);
        return Created($"api/boards/{boardId}/groups/{group.Id}", group.ToDto());
    }

    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    [HttpPut("api/boards/{boardId:guid}/groups/{groupId:guid}")]
    public async Task<ActionResult<EpicGroupDto>> Update(Guid boardId, Guid groupId, UpdateEpicGroupRequest request, CancellationToken ct)
    {
        var group = await db.EpicGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.BoardId == boardId, ct);
        if (group is null) return NotFound();
        group.Name = request.Name;
        UseClientVersion(db, group, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null) return conflict;
        await notifier.BoardEntityChanged(boardId, EntityTypes.EpicGroup, groupId, ChangeKind.Updated, nameof(group.Name));
        return group.ToDto();
    }

    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    [HttpPut("api/boards/{boardId:guid}/groups/{groupId:guid}/rank")]
    public async Task<ActionResult<EpicGroupDto>> Rerank(Guid boardId, Guid groupId, RerankEpicGroupRequest request, CancellationToken ct)
    {
        var group = await db.EpicGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.BoardId == boardId, ct);
        if (group is null) return NotFound();
        var siblings = await EpicRanking.Siblings(db, boardId, null, ct);
        if (!EpicRanking.Move(siblings, groupId, request.PrecedingId, request.FollowingId))
            return BadRequest("The neighbors must be groups or ungrouped epics on this board.");
        UseClientVersion(db, group, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null) return conflict;
        await notifier.BoardEntityChanged(boardId, EntityTypes.EpicGroup, groupId, ChangeKind.Updated, nameof(group.Rank));
        return group.ToDto();
    }

    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    [HttpDelete("api/boards/{boardId:guid}/groups/{groupId:guid}")]
    public async Task<IActionResult> Delete(Guid boardId, Guid groupId, [FromQuery] uint version, CancellationToken ct)
    {
        var group = await db.EpicGroups.Include(g => g.Epics)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.BoardId == boardId, ct);
        if (group is null) return NotFound();
        var siblings = await EpicRanking.Siblings(db, boardId, null, ct);
        var index = siblings.FindIndex(i => i.Id == groupId);
        siblings.RemoveAt(index);
        var epics = group.Epics.OrderBy(e => e.Rank).ThenBy(e => e.Id).ToList();
        // Replace the group in place, preserving its children's relative order.
        siblings.InsertRange(index, epics.Select(EpicRankItem.For));
        var ranks = RankKeys.Rebalanced(siblings.Count);
        for (var i = 0; i < siblings.Count; i++) siblings[i].Rank = ranks[i];
        foreach (var epic in epics) epic.GroupId = null;
        UseClientVersion(db, group, version);
        db.EpicGroups.Remove(group);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null) return conflict;
        await notifier.BoardEntityChanged(boardId, EntityTypes.EpicGroup, groupId, ChangeKind.Deleted);
        foreach (var epic in epics)
            await notifier.BoardEntityChanged(boardId, EntityTypes.Epic, epic.Id, ChangeKind.Updated, nameof(epic.GroupId), nameof(epic.Rank));
        return NoContent();
    }
}
