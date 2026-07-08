using Solo.Api.Services;
using Xunit;

namespace Solo.Api.Tests;

public class RankingTests
{
    private sealed class Item(decimal rank)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public decimal Rank { get; set; } = rank;
    }

    private static bool Move(List<Item> items, Item moved, Item? preceding, Item? following) =>
        Ranking.TryApplyMove(items, moved.Id, preceding?.Id, following?.Id,
            i => i.Id, i => i.Rank, (i, r) => i.Rank = r);

    [Fact]
    public void MoveBetweenNeighbors_OnlyTouchesTheMovedRow()
    {
        var a = new Item(1024m);
        var b = new Item(2048m);
        var c = new Item(3072m);
        var items = new List<Item> { a, b, c };

        Assert.True(Move(items, c, a, b));

        Assert.Equal(1024m, a.Rank);
        Assert.Equal(2048m, b.Rank);
        Assert.InRange(c.Rank, 1025m, 2047m);
    }

    [Fact]
    public void MoveToHead_LandsBeforeEverything()
    {
        var a = new Item(1024m);
        var b = new Item(2048m);
        var items = new List<Item> { a, b };

        Assert.True(Move(items, b, preceding: null, following: null));
        Assert.True(b.Rank < a.Rank);
    }

    [Fact]
    public void MoveBeforeFollowing_WithoutPreceding_LandsBeforeIt()
    {
        var a = new Item(1024m);
        var b = new Item(2048m);
        var c = new Item(3072m);
        var items = new List<Item> { a, b, c };

        Assert.True(Move(items, c, preceding: null, following: b));
        Assert.True(c.Rank > a.Rank && c.Rank < b.Rank);
    }

    [Fact]
    public void ExhaustedMidpoints_RebalanceTheWholeScope()
    {
        var a = new Item(1.000001m);
        var b = new Item(1.000002m);
        var c = new Item(9999m);
        var items = new List<Item> { a, b, c };

        Assert.True(Move(items, c, a, b));

        // Everything re-spaced, order a < c < b preserved.
        Assert.True(a.Rank < c.Rank && c.Rank < b.Rank);
        Assert.True(b.Rank - c.Rank >= 1m && c.Rank - a.Rank >= 1m);
    }

    [Fact]
    public void UnknownNeighbor_IsRejected()
    {
        var a = new Item(1024m);
        var items = new List<Item> { a };

        Assert.False(Ranking.TryApplyMove(items, a.Id, Guid.NewGuid(), null,
            i => i.Id, i => i.Rank, (i, r) => i.Rank = r));
    }

    [Fact]
    public void AppendRank_GrowsPastTheMaximum()
    {
        Assert.Equal(1024m, Ranking.ForAppend([]));
        Assert.True(Ranking.ForAppend([1024m, 5000m]) > 5000m);
    }
}
