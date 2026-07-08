namespace Solo.Data.Domain;

/// <summary>
/// Gapped/fractional ordering keys: a drag-and-drop reorder updates only the
/// moved row. When midpoints run out of precision the caller rebalances the
/// sibling scope (see <c>RebalanceRanks</c>) and retries.
/// </summary>
public static class RankKeys
{
    public const decimal Gap = 1024m;

    /// <summary>Ranks are stored as numeric(18,6); stay above the smallest step.</summary>
    private const decimal MinStep = 0.000002m;

    public static decimal First => Gap;

    public static decimal After(decimal maxRank) => maxRank + Gap;

    /// <summary>
    /// Computes a rank strictly between the two neighbors (null = head/tail).
    /// Returns false when precision is exhausted and the scope must be rebalanced.
    /// </summary>
    public static bool TryBetween(decimal? preceding, decimal? following, out decimal rank)
    {
        switch (preceding, following)
        {
            case (null, null):
                rank = First;
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

    /// <summary>Evenly re-spaced ranks for an ordered sibling list.</summary>
    public static IReadOnlyList<decimal> Rebalanced(int count) =>
        Enumerable.Range(1, count).Select(i => i * Gap).ToList();
}
