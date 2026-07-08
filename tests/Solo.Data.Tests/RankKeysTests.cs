using Solo.Data.Domain;
using Xunit;

namespace Solo.Data.Tests;

public class RankKeysTests
{
    [Fact]
    public void FirstItem_GetsTheBaseGap()
    {
        Assert.True(RankKeys.TryBetween(null, null, out var rank));
        Assert.Equal(RankKeys.Gap, rank);
    }

    [Fact]
    public void HeadInsert_GoesBelowTheFollowing()
    {
        Assert.True(RankKeys.TryBetween(null, 1024m, out var rank));
        Assert.True(rank < 1024m);
    }

    [Fact]
    public void TailInsert_GoesAboveThePreceding()
    {
        Assert.True(RankKeys.TryBetween(2048m, null, out var rank));
        Assert.True(rank > 2048m);
    }

    [Fact]
    public void MidInsert_LandsStrictlyBetween()
    {
        Assert.True(RankKeys.TryBetween(1024m, 2048m, out var rank));
        Assert.InRange(rank, 1024.000001m, 2047.999999m);
    }

    [Fact]
    public void ExhaustedPrecision_SignalsRebalance()
    {
        // Neighbors closer than the numeric(18,6) resolution.
        Assert.False(RankKeys.TryBetween(1.000001m, 1.000002m, out _));
    }

    [Fact]
    public void Rebalanced_ProducesEvenAscendingGaps()
    {
        var ranks = RankKeys.Rebalanced(3);
        Assert.Equal([1024m, 2048m, 3072m], ranks);
    }
}
