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
        Action<T, decimal> setRank) =>
        TryApplyBlockMove(rowsByRank, [movedId], precedingId, followingId, id, getRank, setRank);

    /// <summary>
    /// <see cref="TryApplyMove{T}"/> for several rows at once: they land together
    /// at the target, one after another in the order they already had, however
    /// far apart they started. Only the moved rows are rewritten unless the gap
    /// can't hold them all, when the whole roadmap is re-spaced. False when a
    /// moved row isn't on the roadmap, or a neighbor isn't one of the rows staying put.
    /// </summary>
    public static bool TryApplyBlockMove<T>(
        IList<T> rowsByRank,
        IReadOnlyCollection<Guid> movedIds,
        Guid? precedingId,
        Guid? followingId,
        Func<T, Guid> id,
        Func<T, decimal> getRank,
        Action<T, decimal> setRank)
    {
        var moved = rowsByRank.Where(r => movedIds.Contains(id(r))).ToList();
        if (moved.Count == 0 || moved.Count != movedIds.Distinct().Count())
        {
            return false;
        }

        var others = rowsByRank.Where(r => !movedIds.Contains(id(r))).ToList();
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
        if (TrySpread(before, after, moved.Count, out var ranks))
        {
            for (var i = 0; i < moved.Count; i++)
            {
                setRank(moved[i], ranks[i]);
            }

            return true;
        }

        others.InsertRange(insertAt, moved);
        for (var i = 0; i < others.Count; i++)
        {
            setRank(others[i], (i + 1) * Gap);
        }

        return true;
    }

    /// <summary>
    /// <paramref name="count"/> ranks evenly spaced strictly between two neighbors
    /// (null = an end); false when precision is spent.
    /// </summary>
    private static bool TrySpread(decimal? preceding, decimal? following, int count, out decimal[] ranks)
    {
        if (count == 1)
        {
            var ok = TryBetween(preceding, following, out var rank);
            ranks = [rank];
            return ok;
        }

        ranks = new decimal[count];
        for (var i = 0; i < count; i++)
        {
            ranks[i] = (preceding, following) switch
            {
                (null, null) => (i + 1) * Gap,
                (null, { } f) => f - (count - i) * Gap,
                ({ } p, null) => p + (i + 1) * Gap,
                ({ } p, { } f) => decimal.Round(p + (f - p) * (i + 1) / (count + 1), 6),
            };
        }

        // Rounded to the column's six places, a gap too narrow for them all
        // gives two rows the same rank, or one its neighbor's.
        var line = ranks.Prepend(preceding ?? decimal.MinValue).Append(following ?? decimal.MaxValue).ToList();
        return line.Zip(line.Skip(1)).All(pair => pair.First < pair.Second);
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
