using Solo.Roadmap.Data;

namespace Solo.Roadmap.Tests;

public class RoadmapRankingTests
{
    private sealed class Row(decimal rank)
    {
        public Guid Id { get; } = Guid.NewGuid();

        public decimal Rank { get; set; } = rank;
    }

    private static bool Move(List<Row> rows, Row moved, Row? preceding, Row? following) =>
        RoadmapRanking.TryApplyMove(rows, moved.Id, preceding?.Id, following?.Id,
            r => r.Id, r => r.Rank, (r, rank) => r.Rank = rank);

    [Fact]
    public void A_move_between_rows_only_touches_the_moved_row()
    {
        var a = new Row(1024m);
        var b = new Row(2048m);
        var c = new Row(3072m);

        Assert.True(Move([a, b, c], c, a, b));

        Assert.Equal((1024m, 2048m), (a.Rank, b.Rank));
        Assert.InRange(c.Rank, 1025m, 2047m);
    }

    [Fact]
    public void A_move_to_the_top_lands_before_everything()
    {
        var a = new Row(1024m);
        var b = new Row(2048m);

        Assert.True(Move([a, b], b, preceding: null, following: null));

        Assert.True(b.Rank < a.Rank);
    }

    [Fact]
    public void A_move_to_the_end_lands_after_everything()
    {
        var a = new Row(1024m);
        var b = new Row(2048m);

        Assert.True(Move([a, b], a, preceding: b, following: null));

        Assert.True(a.Rank > b.Rank);
    }

    [Fact]
    public void A_following_neighbor_alone_places_the_row_just_before_it()
    {
        var a = new Row(1024m);
        var b = new Row(2048m);
        var c = new Row(3072m);

        Assert.True(Move([a, b, c], c, preceding: null, following: b));

        Assert.True(c.Rank > a.Rank && c.Rank < b.Rank);
    }

    [Fact]
    public void Exhausted_midpoints_rebalance_the_whole_roadmap_in_the_new_order()
    {
        var a = new Row(1.000001m);
        var b = new Row(1.000002m);
        var c = new Row(9999m);

        Assert.True(Move([a, b, c], c, a, b));

        Assert.Equal((1024m, 2048m, 3072m), (a.Rank, c.Rank, b.Rank));
    }

    /// <summary>Repeatedly halving one gap eventually has to rebalance — and order always holds.</summary>
    [Fact]
    public void Order_survives_many_moves_into_the_same_gap()
    {
        var first = new Row(1024m);
        var last = new Row(2048m);
        var rows = new List<Row> { first, last };
        for (var i = 0; i < 40; i++)
        {
            var row = new Row(RoadmapRanking.ForAppend(rows.Select(r => r.Rank)));
            rows.Add(row);
            Assert.True(Move(rows, row, first, rows.OrderBy(r => r.Rank).Skip(1).First()));
            rows = rows.OrderBy(r => r.Rank).ToList();
            Assert.Equal(first, rows[0]);
            Assert.Equal(row, rows[1]);
            Assert.Equal(last, rows[^1]);
        }
    }

    [Fact]
    public void An_unknown_neighbor_or_row_is_refused()
    {
        var a = new Row(1024m);

        Assert.False(RoadmapRanking.TryApplyMove([a], a.Id, Guid.NewGuid(), null, r => r.Id, r => r.Rank, (_, _) => { }));
        Assert.False(RoadmapRanking.TryApplyMove([a], a.Id, null, Guid.NewGuid(), r => r.Id, r => r.Rank, (_, _) => { }));
        Assert.False(RoadmapRanking.TryApplyMove([a], Guid.NewGuid(), null, null, r => r.Id, r => r.Rank, (_, _) => { }));
    }

    private static bool MoveBlock(List<Row> rows, IEnumerable<Row> moved, Row? preceding, Row? following) =>
        RoadmapRanking.TryApplyBlockMove(rows, moved.Select(r => r.Id).ToList(), preceding?.Id, following?.Id,
            r => r.Id, r => r.Rank, (r, rank) => r.Rank = rank);

    private static List<Row> InOrder(params Row[] rows) => rows.OrderBy(r => r.Rank).ToList();

    [Fact]
    public void A_block_lands_together_between_its_neighbors_in_the_order_it_had()
    {
        var (a, b, c, d, e) = (new Row(1024m), new Row(2048m), new Row(3072m), new Row(4096m), new Row(5120m));

        // a and c, apart, go between d and e; only they are rewritten.
        Assert.True(MoveBlock([a, b, c, d, e], [c, a], d, e));

        Assert.Equal([b, d, a, c, e], InOrder(a, b, c, d, e));
        Assert.Equal((2048m, 4096m, 5120m), (b.Rank, d.Rank, e.Rank));
    }

    [Fact]
    public void A_block_goes_to_the_top_or_the_end()
    {
        var (a, b, c, d) = (new Row(1024m), new Row(2048m), new Row(3072m), new Row(4096m));

        Assert.True(MoveBlock([a, b, c, d], [b, d], preceding: null, following: null));
        Assert.Equal([b, d, a, c], InOrder(a, b, c, d));

        Assert.True(MoveBlock(InOrder(a, b, c, d), [b, d], preceding: c, following: null));
        Assert.Equal([a, c, b, d], InOrder(a, b, c, d));
    }

    [Fact]
    public void A_block_too_big_for_its_gap_rebalances_the_whole_roadmap_in_the_new_order()
    {
        var (a, b) = (new Row(1.000001m), new Row(1.000003m));
        var (c, d) = (new Row(9998m), new Row(9999m));

        Assert.True(MoveBlock([a, b, c, d], [c, d], a, b));

        Assert.Equal((1024m, 2048m, 3072m, 4096m), (a.Rank, c.Rank, d.Rank, b.Rank));
    }

    [Fact]
    public void A_block_with_a_neighbor_inside_it_or_a_row_not_on_the_roadmap_is_refused()
    {
        var (a, b, c) = (new Row(1024m), new Row(2048m), new Row(3072m));

        Assert.False(MoveBlock([a, b, c], [a, b], b, c));
        Assert.False(MoveBlock([a, b, c], [a, new Row(0m)], c, null));
        Assert.Equal((1024m, 2048m, 3072m), (a.Rank, b.Rank, c.Rank));
    }

    [Fact]
    public void An_appended_row_goes_after_the_last()
    {
        Assert.Equal(1024m, RoadmapRanking.ForAppend([]));
        Assert.Equal(6024m, RoadmapRanking.ForAppend([1024m, 5000m]));
    }
}
