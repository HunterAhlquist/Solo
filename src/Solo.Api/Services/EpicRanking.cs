using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Services;

/// <summary>Groups and ungrouped epics share one sibling order.</summary>
internal sealed class EpicRankItem(Guid id, Func<decimal> read, Action<decimal> write)
{
    public Guid Id => id;
    public decimal Rank { get => read(); set => write(value); }
    public static EpicRankItem For(Epic epic) => new(epic.Id, () => epic.Rank, rank => epic.Rank = rank);
    public static EpicRankItem For(EpicGroup group) => new(group.Id, () => group.Rank, rank => group.Rank = rank);
}

internal static class EpicRanking
{
    public static async Task<List<EpicRankItem>> Siblings(SoloDbContext db, Guid boardId, Guid? groupId, CancellationToken ct)
    {
        var epics = await db.Epics.Where(e => e.BoardId == boardId && e.GroupId == groupId).ToListAsync(ct);
        var items = epics.Select(EpicRankItem.For).ToList();
        if (groupId is null)
        {
            var groups = await db.EpicGroups.Where(g => g.BoardId == boardId).ToListAsync(ct);
            items.AddRange(groups.Select(EpicRankItem.For));
        }
        return items.OrderBy(i => i.Rank).ThenBy(i => i.Id).ToList();
    }

    public static bool Move(List<EpicRankItem> siblings, Guid id, Guid? preceding, Guid? following) =>
        (preceding is null || siblings.Any(i => i.Id == preceding && i.Id != id)) &&
        (following is null || siblings.Any(i => i.Id == following && i.Id != id)) &&
        Ranking.TryApplyMove(siblings, id, preceding, following, i => i.Id, i => i.Rank, (i, rank) => i.Rank = rank);
}
