using Microsoft.EntityFrameworkCore;

namespace Solo.DesignDocs.Data;

/// <summary>
/// A document's stored updates: appended as editors send them, read back for a
/// sync, and folded into one snapshot when a writer compacts.
/// </summary>
/// <remarks>
/// Nothing here decodes an update. Callers serialize per document
/// (<c>DocumentLocks</c>), which this class relies on for "the newest
/// snapshot" and "the entries after it" to describe one consistent log.
/// </remarks>
public sealed class DocumentLog(DesignDocsDbContext db)
{
    /// <summary>
    /// What a client that already has everything through
    /// <paramref name="afterSeq"/> is missing, oldest first: the snapshot when
    /// it's newer than that, then the updates after both.
    /// </summary>
    public async Task<IReadOnlyList<DocUpdate>> ReadAsync(Guid documentId, long afterSeq, CancellationToken ct)
    {
        var snapshot = await db.DocUpdates.AsNoTracking()
            .Where(u => u.DocumentId == documentId && u.IsSnapshot)
            .OrderByDescending(u => u.Seq)
            .FirstOrDefaultAsync(ct);

        var from = afterSeq;
        var entries = new List<DocUpdate>();
        if (snapshot is not null && snapshot.Seq > afterSeq)
        {
            entries.Add(snapshot);
            from = snapshot.Seq;
        }

        entries.AddRange(await db.DocUpdates.AsNoTracking()
            .Where(u => u.DocumentId == documentId && !u.IsSnapshot && u.Seq > from)
            .OrderBy(u => u.Seq)
            .ToListAsync(ct));
        return entries;
    }

    /// <summary>The newest seq in the log, or 0 for an empty one.</summary>
    public async Task<long> LatestSeqAsync(Guid documentId, CancellationToken ct) =>
        await db.DocUpdates.Where(u => u.DocumentId == documentId).MaxAsync(u => (long?)u.Seq, ct) ?? 0;

    public Task<bool> IsEmptyAsync(Guid documentId, CancellationToken ct) =>
        db.DocUpdates.AllAsync(u => u.DocumentId != documentId, ct);

    /// <summary>Entries since the last snapshot, which is what decides when a writer should compact.</summary>
    public async Task<int> UpdatesSinceSnapshotAsync(Guid documentId, CancellationToken ct)
    {
        var snapshotSeq = await db.DocUpdates
            .Where(u => u.DocumentId == documentId && u.IsSnapshot)
            .MaxAsync(u => (long?)u.Seq, ct) ?? 0;
        return await db.DocUpdates.CountAsync(u => u.DocumentId == documentId && u.Seq > snapshotSeq, ct);
    }

    /// <summary>
    /// Stores an update and returns its seq. With <paramref name="seedOf"/>,
    /// the update is that note's seed, and its <c>SeedMarkdown</c> is cleared in
    /// the same transaction, so a seed is never stored without the note
    /// knowing it has been seeded, or the other way round.
    /// </summary>
    public async Task<long> AppendAsync(
        Guid documentId, Guid vaultId, Guid userId, byte[] data, Guid? seedOf, CancellationToken ct)
    {
        var update = new DocUpdate { DocumentId = documentId, VaultId = vaultId, UserId = userId, Data = data };
        if (seedOf is not { } noteId)
        {
            db.DocUpdates.Add(update);
            await db.SaveChangesAsync(ct);
            db.Entry(update).State = EntityState.Detached;
            return update.Seq;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.DocUpdates.Add(update);
        await db.SaveChangesAsync(ct);
        await db.Notes.Where(n => n.Id == noteId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.SeedMarkdown, (string?)null), ct);
        await transaction.CommitAsync(ct);
        db.Entry(update).State = EntityState.Detached;
        return update.Seq;
    }

    /// <summary>
    /// Replaces every entry through <paramref name="uptoSeq"/> with one
    /// snapshot stored under that seq. Ignored, returning false, when the
    /// current snapshot is already as new, or when <paramref name="uptoSeq"/>
    /// isn't one of this document's entries.
    /// </summary>
    /// <remarks>
    /// Writers compact on their own, without electing one, so two can race. The
    /// rule keeps whichever covers more, and either result is a valid state:
    /// both snapshots hold everything through their seq.
    /// </remarks>
    public async Task<bool> CompactAsync(
        Guid documentId, Guid vaultId, Guid userId, long uptoSeq, byte[] snapshot, CancellationToken ct)
    {
        // The seq has to be this document's: it's reused for the snapshot, and
        // seqs are unique across every document's log.
        if (!await db.DocUpdates.AnyAsync(u => u.DocumentId == documentId && u.Seq == uptoSeq && !u.IsSnapshot, ct))
        {
            return false;
        }

        var current = await db.DocUpdates
            .Where(u => u.DocumentId == documentId && u.IsSnapshot)
            .MaxAsync(u => (long?)u.Seq, ct) ?? 0;
        if (current >= uptoSeq)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.DocUpdates.Where(u => u.DocumentId == documentId && u.Seq <= uptoSeq).ExecuteDeleteAsync(ct);
        var row = new DocUpdate
        {
            Seq = uptoSeq,
            DocumentId = documentId,
            VaultId = vaultId,
            UserId = userId,
            IsSnapshot = true,
            Data = snapshot,
        };
        db.DocUpdates.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(row).State = EntityState.Detached;
        return true;
    }

    /// <summary>Every entry of the given documents, for deleting them with the note or folder they belong to.</summary>
    public Task<int> DeleteAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct) =>
        db.DocUpdates.Where(u => documentIds.Contains(u.DocumentId)).ExecuteDeleteAsync(ct);
}
