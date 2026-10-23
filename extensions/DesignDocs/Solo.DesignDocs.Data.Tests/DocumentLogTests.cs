using Microsoft.EntityFrameworkCore;

namespace Solo.DesignDocs.Data.Tests;

/// <summary>A document's update log: what a sync reads back, and the rules compaction follows.</summary>
[Collection("postgres")]
public sealed class DocumentLogTests(PostgresFixture postgres)
{
    private async Task<(DesignDocsDbContext Db, DocumentLog Log, Vault Vault, Note Note)> SetUpAsync(string? seed = null)
    {
        var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var vault = new Vault { ProjectId = Guid.NewGuid(), Name = "Docs" };
        var note = new Note { VaultId = vault.Id, Name = "N", SeedMarkdown = seed };
        db.AddRange(vault, note);
        await db.SaveChangesAsync();
        return (db, new DocumentLog(db), vault, note);
    }

    [SkippableFact]
    public async Task A_sync_reads_what_the_client_is_missing_in_order()
    {
        postgres.SkipUnlessDockerAvailable();
        var (db, log, vault, note) = await SetUpAsync();
        await using var _ = db;
        var user = Guid.NewGuid();
        var seqs = new List<long>();
        for (byte i = 1; i <= 4; i++)
        {
            seqs.Add(await log.AppendAsync(note.Id, vault.Id, user, [i], null, default));
        }

        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal([1, 2, 3, 4], (await log.ReadAsync(note.Id, 0, default)).Select(e => e.Data[0]));
        Assert.Equal([3, 4], (await log.ReadAsync(note.Id, seqs[1], default)).Select(e => e.Data[0]));
        Assert.Empty(await log.ReadAsync(note.Id, seqs[3], default));
        Assert.Equal(seqs[3], await log.LatestSeqAsync(note.Id, default));
        Assert.Equal(4, await log.UpdatesSinceSnapshotAsync(note.Id, default));
    }

    [SkippableFact]
    public async Task Compacting_replaces_the_entries_it_covers_with_one_snapshot_under_the_same_seq()
    {
        postgres.SkipUnlessDockerAvailable();
        var (db, log, vault, note) = await SetUpAsync();
        await using var _ = db;
        var user = Guid.NewGuid();
        var first = await log.AppendAsync(note.Id, vault.Id, user, [1], null, default);
        var second = await log.AppendAsync(note.Id, vault.Id, user, [2], null, default);
        var third = await log.AppendAsync(note.Id, vault.Id, user, [3], null, default);

        Assert.True(await log.CompactAsync(note.Id, vault.Id, user, second, [12], default));

        var all = await log.ReadAsync(note.Id, 0, default);
        Assert.Equal([(second, true, (byte)12), (third, false, (byte)3)], all.Select(e => (e.Seq, e.IsSnapshot, e.Data[0])));
        // A client that had everything through the first entry gets the snapshot, not a gap.
        Assert.Equal([second, third], (await log.ReadAsync(note.Id, first, default)).Select(e => e.Seq));
        // One that had the second already needs only the third.
        Assert.Equal([third], (await log.ReadAsync(note.Id, second, default)).Select(e => e.Seq));
        Assert.Equal(1, await log.UpdatesSinceSnapshotAsync(note.Id, default));

        // New entries keep counting up past the reused seq.
        Assert.True(await log.AppendAsync(note.Id, vault.Id, user, [4], null, default) > third);
    }

    [SkippableFact]
    public async Task An_older_or_foreign_compaction_is_ignored()
    {
        postgres.SkipUnlessDockerAvailable();
        var (db, log, vault, note) = await SetUpAsync();
        await using var _ = db;
        var other = new Note { VaultId = vault.Id, Name = "Other" };
        db.Notes.Add(other);
        await db.SaveChangesAsync();
        var user = Guid.NewGuid();
        var first = await log.AppendAsync(note.Id, vault.Id, user, [1], null, default);
        var foreign = await log.AppendAsync(other.Id, vault.Id, user, [9], null, default);
        var second = await log.AppendAsync(note.Id, vault.Id, user, [2], null, default);

        Assert.True(await log.CompactAsync(note.Id, vault.Id, user, second, [22], default));
        // Two writers raced; the one covering less loses.
        Assert.False(await log.CompactAsync(note.Id, vault.Id, user, first, [11], default));
        // A seq from another document would collide with that document's entry.
        Assert.False(await log.CompactAsync(note.Id, vault.Id, user, foreign, [99], default));

        Assert.Equal([(byte)22], (await log.ReadAsync(note.Id, 0, default)).Select(e => e.Data[0]));
        Assert.Equal([(byte)9], (await log.ReadAsync(other.Id, 0, default)).Select(e => e.Data[0]));
    }

    [SkippableFact]
    public async Task A_seed_update_clears_the_notes_seed_with_it()
    {
        postgres.SkipUnlessDockerAvailable();
        var (db, log, vault, note) = await SetUpAsync(seed: "# Imported");
        await using var _ = db;
        Assert.True(await log.IsEmptyAsync(note.Id, default));

        await log.AppendAsync(note.Id, vault.Id, Guid.NewGuid(), [1], note.Id, default);

        Assert.False(await log.IsEmptyAsync(note.Id, default));
        Assert.Null(await db.Notes.Where(n => n.Id == note.Id).Select(n => n.SeedMarkdown).SingleAsync());
    }

    [SkippableFact]
    public async Task Deleting_documents_removes_only_their_entries()
    {
        postgres.SkipUnlessDockerAvailable();
        var (db, log, vault, note) = await SetUpAsync();
        await using var _ = db;
        var other = Guid.NewGuid();
        await log.AppendAsync(note.Id, vault.Id, Guid.NewGuid(), [1], null, default);
        await log.AppendAsync(other, vault.Id, Guid.NewGuid(), [2], null, default);

        Assert.Equal(1, await log.DeleteAsync([note.Id], default));
        Assert.True(await log.IsEmptyAsync(note.Id, default));
        Assert.False(await log.IsEmptyAsync(other, default));
    }
}
