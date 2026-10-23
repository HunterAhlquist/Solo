using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;

namespace Solo.DesignDocs.Live;

/// <summary>
/// The relay: stores and forwards what editors send on a document's topic,
/// without ever decoding it. See <see cref="SyncProtocol"/> for the messages.
/// </summary>
/// <remarks>
/// <para>Core has already checked that the sender holds the topic, which needs
/// read access to the project the client named. This handler adds the rest:
/// the document has to belong to that project, writes need a writer's role
/// and an open vault, and an imported note takes exactly one seed.</para>
///
/// <para>Created per message in the hub invocation's scope. It reads nothing
/// request-shaped, so <see cref="ISoloQuery"/> isn't used here: who sent the
/// message is in <see cref="LiveMessage.Sender"/>, kept current by core.</para>
/// </remarks>
public sealed class DocumentSyncHandler(
    DesignDocsDbContext db,
    DocumentLog log,
    DocumentLocks locks,
    DocumentDirectory directory,
    SeedLeases leases,
    PartAssembler parts,
    ILogger<DocumentSyncHandler> logger) : ISoloLiveMessageHandler
{
    /// <summary>A sketch's "last edited" is written at most this often per person, rather than per stroke.</summary>
    private static readonly TimeSpan SketchEditedEvery = TimeSpan.FromSeconds(30);

    public async ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct)
    {
        if (DocsTopics.DocumentIdOf(message.Topic) is not { } documentId)
        {
            return;
        }

        // A document under another project's id is dropped without a word, so
        // a member of one project can't probe another's documents through it.
        var document = await directory.FindAsync(db, documentId, ct);
        if (document is null || document.ProjectId != message.ProjectId)
        {
            return;
        }

        var (kind, data) = (message.Kind, message.Data);
        if (kind == SyncProtocol.Part)
        {
            if (SyncProtocol.Read<PartMessage>(data) is not { } part ||
                parts.Add(message.Sender.UserId, documentId, part) is not { } whole)
            {
                return;
            }

            (kind, data) = whole;
        }

        switch (kind)
        {
            case SyncProtocol.Sync:
                await SyncAsync(document, message.Sender, SyncProtocol.Read<SyncMessage>(data), reply, ct);
                break;
            case SyncProtocol.Update:
                await UpdateAsync(document, message.Sender, SyncProtocol.Read<UpdateMessage>(data), reply, ct);
                break;
            case SyncProtocol.Awareness:
                // Readers have no cursor to show, so theirs never reaches anyone.
                if (message.Sender.CanWrite && data is not null)
                {
                    await SendAsync(reply.ToOthersAsync, SyncProtocol.Awareness, data, ct);
                }

                break;
            case SyncProtocol.Compact:
                await CompactAsync(document, message.Sender, SyncProtocol.Read<CompactMessage>(data), ct);
                break;
        }
    }

    private async Task SyncAsync(
        DocumentInfo document, LiveSender sender, SyncMessage? request, ILiveMessageReply reply, CancellationToken ct)
    {
        var afterSeq = Math.Max(0, request?.AfterSeq ?? 0);
        using var _ = await locks.AcquireAsync(document.DocumentId, ct);

        if (document.IsNote && await SeedOfAsync(document.DocumentId, ct) is { } seed &&
            await log.IsEmptyAsync(document.DocumentId, ct))
        {
            if (!sender.CanWrite || document.VaultClosed)
            {
                // Nobody here can seed it, so show the imported text as it is.
                await SendAsync(reply.ToSenderAsync, SyncProtocol.Unseeded,
                    SyncProtocol.Write(new UnseededMessage(seed)), ct);
                await reply.ToSenderAsync(SyncProtocol.Synced, SyncProtocol.Write(new SyncedMessage(0, 0)), ct);
                return;
            }

            if (leases.TryTake(document.DocumentId, sender.UserId) is { } lease)
            {
                await SendAsync(reply.ToSenderAsync, SyncProtocol.Seed,
                    SyncProtocol.Write(new SeedMessage(seed, lease)), ct);
            }
            else
            {
                await reply.ToSenderAsync(SyncProtocol.Seeding, null, ct);
            }

            return;
        }

        var entries = await log.ReadAsync(document.DocumentId, afterSeq, ct);
        foreach (var batch in Batches(entries))
        {
            await SendAsync(reply.ToSenderAsync, SyncProtocol.State, SyncProtocol.Write(new StateMessage(batch)), ct);
        }

        var latest = entries.Count > 0 ? entries[^1].Seq : await log.LatestSeqAsync(document.DocumentId, ct);
        var pending = await log.UpdatesSinceSnapshotAsync(document.DocumentId, ct);
        await reply.ToSenderAsync(SyncProtocol.Synced, SyncProtocol.Write(new SyncedMessage(latest, pending)), ct);
    }

    private async Task UpdateAsync(
        DocumentInfo document, LiveSender sender, UpdateMessage? update, ILiveMessageReply reply, CancellationToken ct)
    {
        if (update is null || string.IsNullOrEmpty(update.Id) || update.Id.Length > 64)
        {
            return;
        }

        var refusal = !sender.CanWrite ? SyncProtocol.Reasons.ReadOnly
            : document.VaultClosed ? SyncProtocol.Reasons.Closed
            : Decode(update.Data) is null ? SyncProtocol.Reasons.Malformed
            : null;
        if (refusal is not null)
        {
            await Refuse(reply, update.Id, refusal, ct);
            return;
        }

        var bytes = Decode(update.Data)!;
        long seq;
        using (await locks.AcquireAsync(document.DocumentId, ct))
        {
            // The cached answer can be up to a moment old; the vault's own row
            // can't, and a close has to stop the very next write.
            if (await db.Vaults.AsNoTracking().Where(v => v.Id == document.VaultId)
                    .Select(v => v.IsClosed).FirstOrDefaultAsync(ct))
            {
                await Refuse(reply, update.Id, SyncProtocol.Reasons.Closed, ct);
                return;
            }

            Guid? seedOf = null;
            if (document.IsNote && await SeedOfAsync(document.DocumentId, ct) is not null &&
                await log.IsEmptyAsync(document.DocumentId, ct))
            {
                // Waiting for its seed: only the lease holder's seed may land.
                if (update.Lease is null || !leases.IsValid(document.DocumentId, update.Lease))
                {
                    await Refuse(reply, update.Id,
                        update.Lease is null ? SyncProtocol.Reasons.Unseeded : SyncProtocol.Reasons.SeedLost, ct);
                    return;
                }

                seedOf = document.DocumentId;
            }
            else if (update.Lease is not null)
            {
                // Seeded already, by someone else: this copy of the text would
                // be a second one beside theirs.
                await Refuse(reply, update.Id, SyncProtocol.Reasons.SeedLost, ct);
                return;
            }

            seq = await log.AppendAsync(document.DocumentId, document.VaultId, sender.UserId, bytes, seedOf, ct);
            if (seedOf is not null)
            {
                leases.Release(document.DocumentId);
            }

            // Inside the lock, so everyone hears updates in seq order.
            await reply.ToSenderAsync(SyncProtocol.Ack, SyncProtocol.Write(new AckMessage(update.Id, seq)), ct);
            await SendAsync(reply.ToOthersAsync, SyncProtocol.Update,
                SyncProtocol.Write(new RelayedUpdate(seq, update.Data)), ct);
        }

        if (!document.IsNote)
        {
            await MarkSketchEditedAsync(document.DocumentId, sender.UserId, ct);
        }
    }

    private async Task CompactAsync(DocumentInfo document, LiveSender sender, CompactMessage? compact, CancellationToken ct)
    {
        if (compact is null || !sender.CanWrite || document.VaultClosed || Decode(compact.Data) is not { } snapshot)
        {
            return;
        }

        using var _ = await locks.AcquireAsync(document.DocumentId, ct);
        if (!await log.CompactAsync(document.DocumentId, document.VaultId, sender.UserId, compact.UptoSeq, snapshot, ct))
        {
            logger.LogDebug("Compaction of {Document} through {Seq} was ignored: a newer snapshot exists.",
                document.DocumentId, compact.UptoSeq);
        }
    }

    private Task<string?> SeedOfAsync(Guid noteId, CancellationToken ct) =>
        db.Notes.AsNoTracking().Where(n => n.Id == noteId).Select(n => n.SeedMarkdown).FirstOrDefaultAsync(ct);

    private Task MarkSketchEditedAsync(Guid sketchId, Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - SketchEditedEvery;
        return db.Sketches
            .Where(s => s.Id == sketchId &&
                        (s.LastEditedAt == null || s.LastEditedAt < since || s.LastEditedBy != userId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastEditedBy, userId)
                .SetProperty(x => x.LastEditedAt, now), ct);
    }

    private static Task Refuse(ILiveMessageReply reply, string id, string reason, CancellationToken ct) =>
        reply.ToSenderAsync(SyncProtocol.Refused, SyncProtocol.Write(new RefusedMessage(id, reason)), ct);

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        var bytes = new byte[base64.Length * 3 / 4 + 3];
        return Convert.TryFromBase64String(base64, bytes, out var written) ? bytes[..written] : null;
    }

    private static async Task SendAsync(
        Func<string, string?, CancellationToken, Task> send, string kind, string data, CancellationToken ct)
    {
        foreach (var (frameKind, frameData) in SyncProtocol.Frame(kind, data))
        {
            await send(frameKind, frameData, ct);
        }
    }

    /// <summary>
    /// Log entries grouped into state messages that each fit one hub message.
    /// An entry too long for one goes alone, and is split into parts on the way.
    /// </summary>
    private static IEnumerable<IReadOnlyList<StateEntry>> Batches(IReadOnlyList<DocUpdate> entries)
    {
        var batch = new List<StateEntry>();
        var length = 0;
        foreach (var entry in entries)
        {
            var data = Convert.ToBase64String(entry.Data);
            // Each entry costs its data plus a few characters of "[seq,\"\"],".
            var cost = data.Length + 32;
            if (batch.Count > 0 && length + cost > SyncProtocol.ChunkLength)
            {
                yield return batch;
                batch = [];
                length = 0;
            }

            batch.Add(new StateEntry(entry.Seq, data));
            length += cost;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
