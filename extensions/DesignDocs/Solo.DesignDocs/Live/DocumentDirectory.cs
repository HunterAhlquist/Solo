using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Data;

namespace Solo.DesignDocs.Live;

/// <summary>What the live handler needs to know about a document before it touches the log.</summary>
public sealed record DocumentInfo(Guid DocumentId, Guid ProjectId, Guid VaultId, bool IsNote, bool VaultClosed);

/// <summary>
/// Which project and vault each document belongs to, and whether its vault is
/// closed, cached for a short while so typing doesn't cost a lookup per message.
/// </summary>
/// <remarks>
/// The REST side forgets entries when it closes, reopens or deletes a vault, or
/// deletes a document, and the expiry covers anything missed. A stored write
/// doesn't rely on it alone: the handler rereads the vault's closed flag under
/// the document's lock before appending.
/// </remarks>
public sealed class DocumentDirectory(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, (DocumentInfo? Info, DateTimeOffset Expires)> _cache = new();

    /// <summary>The document, or null when there is no such note or sketch.</summary>
    public async Task<DocumentInfo?> FindAsync(DesignDocsDbContext db, Guid documentId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(documentId, out var cached) && cached.Expires > now)
        {
            return cached.Info;
        }

        var info = await db.Notes.AsNoTracking()
                       .Where(n => n.Id == documentId)
                       .Join(db.Vaults, n => n.VaultId, v => v.Id,
                           (n, v) => new DocumentInfo(n.Id, v.ProjectId, v.Id, true, v.IsClosed))
                       .FirstOrDefaultAsync(ct)
                   ?? await db.Sketches.AsNoTracking()
                       .Where(s => s.Id == documentId)
                       .Join(db.Vaults, s => s.VaultId, v => v.Id,
                           (s, v) => new DocumentInfo(s.Id, v.ProjectId, v.Id, false, v.IsClosed))
                       .FirstOrDefaultAsync(ct);

        // A miss is cached briefly too, so a stale tab sending to a deleted
        // document doesn't cost two queries per keystroke.
        _cache[documentId] = (info, now + Lifetime);
        if (_cache.Count > 10_000)
        {
            foreach (var (key, entry) in _cache)
            {
                if (entry.Expires <= now)
                {
                    _cache.TryRemove(key, out _);
                }
            }
        }

        return info;
    }

    public void Forget(Guid documentId) => _cache.TryRemove(documentId, out _);

    public void ForgetVault(Guid vaultId)
    {
        foreach (var (key, entry) in _cache)
        {
            if (entry.Info?.VaultId == vaultId)
            {
                _cache.TryRemove(key, out _);
            }
        }
    }
}
