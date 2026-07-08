using Solo.Data.Domain;

namespace Solo.Api.Services;

/// <summary>
/// Applies a drag-and-drop re-rank server-side: normally only the moved row's
/// rank changes; when fractional midpoints run out of precision the whole
/// sibling scope is rebalanced.
/// </summary>
public static class Ranking
{
    /// <summary>
    /// Moves <paramref name="movedId"/> after <paramref name="precedingId"/>
    /// (or before <paramref name="followingId"/> when no preceding id is given,
    /// or to the head when both are null) within
    /// <paramref name="siblingsByRank"/>, which must be the full sibling scope
    /// ordered by rank (including the moved item). Returns false when an id is
    /// unknown to the scope.
    /// </summary>
    public static bool TryApplyMove<T>(
        IList<T> siblingsByRank,
        Guid movedId,
        Guid? precedingId,
        Guid? followingId,
        Func<T, Guid> id,
        Func<T, decimal> getRank,
        Action<T, decimal> setRank)
    {
        var moved = siblingsByRank.FirstOrDefault(s => id(s) == movedId);
        if (moved is null)
        {
            return false;
        }

        var others = siblingsByRank.Where(s => id(s) != movedId).ToList();
        int insertAt;
        if (precedingId is not null)
        {
            var precedingIndex = others.FindIndex(s => id(s) == precedingId.Value);
            if (precedingIndex < 0)
            {
                return false;
            }

            insertAt = precedingIndex + 1;
        }
        else if (followingId is not null)
        {
            insertAt = others.FindIndex(s => id(s) == followingId.Value);
            if (insertAt < 0)
            {
                return false;
            }
        }
        else
        {
            insertAt = 0;
        }

        decimal? before = insertAt > 0 ? getRank(others[insertAt - 1]) : null;
        decimal? after = insertAt < others.Count ? getRank(others[insertAt]) : null;

        if (RankKeys.TryBetween(before, after, out var rank))
        {
            setRank(moved, rank);
            return true;
        }

        // Precision exhausted: rebalance the whole scope in the new order.
        others.Insert(insertAt, moved);
        var ranks = RankKeys.Rebalanced(others.Count);
        for (var i = 0; i < others.Count; i++)
        {
            setRank(others[i], ranks[i]);
        }

        return true;
    }

    /// <summary>Rank for a newly created item appended to the end of its scope.</summary>
    public static decimal ForAppend(IEnumerable<decimal> existingRanks)
    {
        var ranks = existingRanks.ToList();
        return ranks.Count == 0 ? RankKeys.First : RankKeys.After(ranks.Max());
    }
}
