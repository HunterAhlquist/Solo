using Solo.DesignDocs.Live;

namespace Solo.DesignDocs.Data.Tests;

/// <summary>The live handler's in-memory state: seed leases, part reassembly and per-document locks.</summary>
public sealed class LiveStateTests
{
    private static readonly Guid Doc = Guid.NewGuid();
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    [Fact]
    public void One_writer_holds_a_seed_lease_until_it_runs_out()
    {
        var time = new ManualTime();
        var leases = new SeedLeases(time);

        var alices = leases.TryTake(Doc, Alice);
        Assert.NotNull(alices);
        Assert.Null(leases.TryTake(Doc, Bob));
        Assert.True(leases.IsValid(Doc, alices));

        time.Advance(SeedLeases.Duration + TimeSpan.FromSeconds(1));
        Assert.False(leases.IsValid(Doc, alices));
        var bobs = leases.TryTake(Doc, Bob);
        Assert.NotNull(bobs);
        Assert.False(leases.IsValid(Doc, alices));
    }

    /// <summary>A reload asks again; the old tab's lease stops counting, so only one seed can land.</summary>
    [Fact]
    public void The_same_writer_asking_again_replaces_their_lease()
    {
        var leases = new SeedLeases(new ManualTime());
        var first = leases.TryTake(Doc, Alice)!;
        var second = leases.TryTake(Doc, Alice)!;

        Assert.False(leases.IsValid(Doc, first));
        Assert.True(leases.IsValid(Doc, second));
        leases.Release(Doc);
        Assert.False(leases.IsValid(Doc, second));
    }

    [Fact]
    public void Parts_come_back_together_in_any_order()
    {
        var parts = new PartAssembler(new ManualTime());
        var whole = new string('a', SyncProtocol.ChunkLength) + new string('b', SyncProtocol.ChunkLength) + "c";
        var framed = SyncProtocol.Frame("update", whole).ToList();
        Assert.Equal(3, framed.Count);
        Assert.All(framed, f => Assert.Equal(SyncProtocol.Part, f.Kind));

        var messages = framed.Select(f => SyncProtocol.Read<PartMessage>(f.Data)!).Reverse().ToList();
        Assert.Null(parts.Add(Alice, Doc, messages[0]));
        // A duplicate is harmless.
        Assert.Null(parts.Add(Alice, Doc, messages[0]));
        Assert.Null(parts.Add(Alice, Doc, messages[1]));
        var done = parts.Add(Alice, Doc, messages[2]);

        Assert.Equal(("update", whole), done);
        Assert.Equal(0, parts.Count);
    }

    [Fact]
    public void A_short_message_is_sent_whole()
    {
        Assert.Equal([("sync", "{}")], SyncProtocol.Frame("sync", "{}"));
    }

    [Fact]
    public void Parts_from_different_senders_never_mix()
    {
        var parts = new PartAssembler(new ManualTime());
        Assert.Null(parts.Add(Alice, Doc, new PartMessage("update", "x", 0, 2, "alice-")));
        Assert.Null(parts.Add(Bob, Doc, new PartMessage("update", "x", 1, 2, "bob")));
        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public void Unfinished_messages_expire()
    {
        var time = new ManualTime();
        var parts = new PartAssembler(time);
        parts.Add(Alice, Doc, new PartMessage("update", "x", 0, 2, "half"));

        time.Advance(PartAssembler.Expiry + TimeSpan.FromSeconds(1));
        // The next part arrives too late: the first is gone, so this starts over.
        Assert.Null(parts.Add(Alice, Doc, new PartMessage("update", "x", 1, 2, "other half")));
        Assert.Equal(1, parts.Count);
    }

    [Theory]
    [InlineData("part", 0, 2)]   // a part can't wrap a part
    [InlineData("update", 2, 2)] // index out of range
    [InlineData("update", 0, 0)] // no parts
    [InlineData("update", 0, 100_000)] // more than the cap allows
    public void Malformed_parts_are_dropped(string kind, int index, int count)
    {
        var parts = new PartAssembler(new ManualTime());
        Assert.Null(parts.Add(Alice, Doc, new PartMessage(kind, "x", index, count, "d")));
        Assert.Equal(0, parts.Count);
    }

    [Fact]
    public async Task A_documents_holders_take_turns_and_an_idle_document_holds_no_lock()
    {
        var locks = new DocumentLocks();
        var order = new List<int>();
        var first = await locks.AcquireAsync(Doc, default);
        var waiting = Task.Run(async () =>
        {
            using var _ = await locks.AcquireAsync(Doc, default);
            lock (order)
            {
                order.Add(2);
            }
        });

        await Task.Delay(100);
        lock (order)
        {
            order.Add(1);
        }

        first.Dispose();
        await waiting;

        Assert.Equal([1, 2], order);
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Different_documents_never_wait_for_each_other()
    {
        var locks = new DocumentLocks();
        using var one = await locks.AcquireAsync(Guid.NewGuid(), default);
        using var other = await locks.AcquireAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, locks.Count);
    }
}
