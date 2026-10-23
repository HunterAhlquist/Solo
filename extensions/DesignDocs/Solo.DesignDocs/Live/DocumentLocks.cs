using System.Collections.Concurrent;

namespace Solo.DesignDocs.Live;

/// <summary>
/// One async lock per open document, so its appends, compactions and sync
/// replies happen one at a time.
/// </summary>
/// <remarks>
/// <para>That ordering is what lets a client trust the stream: everything
/// relayed to it arrives in seq order, and a sync reply can't interleave with
/// an append. A client that resyncs from the highest seq it saw therefore
/// misses nothing.</para>
///
/// <para>Per process. Solo runs as one container, so one process is the whole
/// instance; a second instance would need the lock in Postgres instead.</para>
/// </remarks>
public sealed class DocumentLocks
{
    private readonly ConcurrentDictionary<Guid, Entry> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid documentId, CancellationToken ct)
    {
        while (true)
        {
            var entry = _locks.GetOrAdd(documentId, _ => new Entry());
            lock (entry)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.Users++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(ct);
            }
            catch
            {
                Release(documentId, entry, held: false);
                throw;
            }

            return new Holder(this, documentId, entry);
        }
    }

    /// <summary>How many documents have a lock right now. For tests: an idle document holds none.</summary>
    public int Count => _locks.Count;

    private void Release(Guid documentId, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (entry)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                // Retired under its own lock, so a caller that fetched it just
                // now sees that and fetches a fresh one instead.
                entry.Retired = true;
                _locks.TryRemove(new KeyValuePair<Guid, Entry>(documentId, entry));
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
        public bool Retired;
    }

    private sealed class Holder(DocumentLocks owner, Guid documentId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(documentId, entry, held: true);
            }
        }
    }
}
