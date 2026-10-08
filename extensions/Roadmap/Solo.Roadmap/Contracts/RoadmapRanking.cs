namespace Solo.Roadmap.Contracts;

/// <summary>
/// Fractional midpoint ranking for a roadmap's rows. Entries sharing a rank
/// share a row, so a rank places a row, not an entry: a move rewrites only the
/// entries moved until precision runs out, then the whole roadmap is re-spaced.
/// </summary>
/// <remarks>
/// <para>A port of core's <c>Ranking</c>/<c>RankKeys</c> rather than a reference to
/// them — an extension may not reference Solo.Api or Solo.Data. The numbers
/// match core's so the two behave the same under the same storage precision
/// (numeric(18,6)).</para>
///
/// <para>Compiled into both halves, like the rest of <c>Contracts/</c>. The page
/// applies a move here exactly as the server is about to, so its rows agree
/// with the server's without reading them back — its own changes aren't echoed
/// to it.</para>
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
    /// Moves <paramref name="movedId"/> to a row of its own after the row of
    /// <paramref name="precedingId"/>, or before the row of
    /// <paramref name="followingId"/> when no preceding id is given, or to the
    /// top when both are null. <paramref name="entries"/> must be the whole
    /// roadmap, moved entry included. False when a neighbor isn't on it.
    /// </summary>
    public static bool TryApplyMove<T>(
        IList<T> entries,
        Guid movedId,
        Guid? precedingId,
        Guid? followingId,
        Func<T, Guid> id,
        Func<T, decimal> getRank,
        Action<T, decimal> setRank) =>
        TryApplyBlockMove(entries, [movedId], precedingId, followingId, id, getRank, setRank);

    /// <summary>
    /// <see cref="TryApplyMove{T}"/> for several entries at once: they land
    /// together at the target, a row after a row in the order they already had,
    /// however far apart they started. Entries that shared a row still share
    /// one; the rest of a row an entry leaves stays where it was. Only the moved
    /// entries are rewritten unless the gap can't hold their rows, when the
    /// whole roadmap is re-spaced. False when a moved entry isn't on the
    /// roadmap, or a neighbor isn't one of the entries staying put.
    /// </summary>
    public static bool TryApplyBlockMove<T>(
        IList<T> entries,
        IReadOnlyCollection<Guid> movedIds,
        Guid? precedingId,
        Guid? followingId,
        Func<T, Guid> id,
        Func<T, decimal> getRank,
        Action<T, decimal> setRank)
    {
        var moved = entries.Where(e => movedIds.Contains(id(e))).ToList();
        if (moved.Count == 0 || moved.Count != movedIds.Distinct().Count())
        {
            return false;
        }

        var others = Rows(entries.Where(e => !movedIds.Contains(id(e))), getRank);
        int insertAt;
        if (precedingId is { } preceding)
        {
            var index = others.FindIndex(row => row.Any(e => id(e) == preceding));
            if (index < 0)
            {
                return false;
            }

            insertAt = index + 1;
        }
        else if (followingId is { } following)
        {
            insertAt = others.FindIndex(row => row.Any(e => id(e) == following));
            if (insertAt < 0)
            {
                return false;
            }
        }
        else
        {
            insertAt = 0;
        }

        var movedRows = Rows(moved, getRank);
        decimal? before = insertAt > 0 ? getRank(others[insertAt - 1][0]) : null;
        decimal? after = insertAt < others.Count ? getRank(others[insertAt][0]) : null;
        if (TrySpread(before, after, movedRows.Count, out var ranks))
        {
            for (var i = 0; i < movedRows.Count; i++)
            {
                movedRows[i].ForEach(e => setRank(e, ranks[i]));
            }

            return true;
        }

        others.InsertRange(insertAt, movedRows);
        for (var i = 0; i < others.Count; i++)
        {
            var rank = (i + 1) * Gap;
            others[i].ForEach(e => setRank(e, rank));
        }

        return true;
    }

    /// <summary>
    /// Puts entries on the row <paramref name="joinId"/> is on, by giving them
    /// its rank; nothing else moves. Whether their tasks fit beside the ones
    /// already there is the page's to judge — dates live on the tasks and
    /// change under a row anyway. False when a moved entry or the target isn't
    /// on the roadmap, or the target is one of the entries moving.
    /// </summary>
    public static bool TryJoin<T>(
        IList<T> entries,
        IReadOnlyCollection<Guid> movedIds,
        Guid joinId,
        Func<T, Guid> id,
        Func<T, decimal> getRank,
        Action<T, decimal> setRank)
    {
        var target = entries.Where(e => id(e) == joinId).Select(e => (decimal?)getRank(e)).FirstOrDefault();
        var moved = entries.Where(e => movedIds.Contains(id(e))).ToList();
        if (target is not { } rank || movedIds.Contains(joinId) ||
            moved.Count == 0 || moved.Count != movedIds.Distinct().Count())
        {
            return false;
        }

        moved.ForEach(e => setRank(e, rank));
        return true;
    }

    /// <summary>Entries gathered into rows, each the entries of one rank, in rank order.</summary>
    private static List<List<T>> Rows<T>(IEnumerable<T> entries, Func<T, decimal> getRank) =>
        entries.GroupBy(getRank).OrderBy(row => row.Key).Select(row => row.ToList()).ToList();

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
