namespace Solo.Roadmap.Data;

/// <summary>
/// Fractional midpoint ranking for a roadmap's rows: a reorder rewrites only the
/// moved row until precision runs out, then the whole roadmap is re-spaced.
/// </summary>
/// <remarks>
/// A port of core's <c>Ranking</c>/<c>RankKeys</c> rather than a reference to
/// them — an extension may not reference Solo.Api or Solo.Data. The numbers
/// match core's so the two behave the same under the same storage precision
/// (numeric(18,6)).
/// </remarks>
public static class RoadmapRanking
{
    public const decimal Gap = 1024m;

    /// <summary>Stay above the smallest step numeric(18,6) can hold.</summary>
    private const decimal MinStep = 0.000002m;

    /// <summary>Rank for a row appended after every existing one.</summary>
    public static decimal ForAppend(IEnumerable<decimal> existingRanks)
    {
        var ranks = existingRanks.ToList();
        return ranks.Count == 0 ? Gap : ranks.Max() + Gap;
    }

    /// <summary>
    /// Moves <paramref name="movedId"/> after <paramref name="precedingId"/>, or
    /// before <paramref name="followingId"/> when no preceding id is given, or to
    /// the top when both are null. <paramref name="rowsByRank"/> must be the whole
    /// roadmap in rank order, moved row included. False when a neighbor isn't on it.
    /// </summary>
    public static bool TryApplyMove<T>(
        IList<T> rowsByRank,
        Guid movedId,
        Guid? precedingId,
        Guid? followingId,
        Func<T, Guid> id,
        Func<T, decimal> getRank,
        Action<T, decimal> setRank)
    {
        var moved = rowsByRank.FirstOrDefault(r => id(r) == movedId);
        if (moved is null)
        {
            return false;
        }

        var others = rowsByRank.Where(r => id(r) != movedId).ToList();
        int insertAt;
        if (precedingId is { } preceding)
        {
            var index = others.FindIndex(r => id(r) == preceding);
            if (index < 0)
            {
                return false;
            }

            insertAt = index + 1;
        }
        else if (followingId is { } following)
        {
            insertAt = others.FindIndex(r => id(r) == following);
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
        if (TryBetween(before, after, out var rank))
        {
            setRank(moved, rank);
            return true;
        }

        others.Insert(insertAt, moved);
        for (var i = 0; i < others.Count; i++)
        {
            setRank(others[i], (i + 1) * Gap);
        }

        return true;
    }

    /// <summary>A rank strictly between two neighbors (null = an end); false when precision is spent.</summary>
    public static bool TryBetween(decimal? preceding, decimal? following, out decimal rank)
    {
        switch (preceding, following)
        {
            case (null, null):
                rank = Gap;
                return true;
            case (null, { } f):
                rank = f - Gap;
                return true;
            case ({ } p, null):
                rank = p + Gap;
                return true;
            case ({ } p, { } f):
                rank = decimal.Round((p + f) / 2m, 6);
                return f - p >= MinStep && rank > p && rank < f;
        }
    }
}
