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

    [Fact]
    public void An_appended_row_goes_after_the_last()
    {
        Assert.Equal(1024m, RoadmapRanking.ForAppend([]));
        Assert.Equal(6024m, RoadmapRanking.ForAppend([1024m, 5000m]));
    }
}
