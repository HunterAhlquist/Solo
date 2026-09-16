using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Solo.Data.Entities;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Bridges core audit saves to extension-owned storage without changing core's
/// model. Registered after the interceptor that creates the audit rows.
/// </summary>
public sealed class ExtensionAuditInterceptor(ExtensionAuditContributions contributions) : SaveChangesInterceptor
{
    private List<AuditEntry> _pending = [];
    private List<ISoloAuditContributor> _writers = [];
    private IDbContextTransaction? _ownedTransaction;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        PrepareAsync(eventData.Context!, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await PrepareAsync(eventData.Context!, cancellationToken);
        return result;
    }

    private async Task PrepareAsync(DbContext db, CancellationToken ct)
    {
        _writers = contributions.Enabled().Select(c => c.Contributor).Where(c => c.ShouldRecord).ToList();
        if (_writers.Count == 0) return;
        _pending = db.ChangeTracker.Entries<AuditEntry>()
            .Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (_pending.Count > 0 && db.Database.CurrentTransaction is null)
            _ownedTransaction = await db.Database.BeginTransactionAsync(ct);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        CompleteAsync(eventData.Context!, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await CompleteAsync(eventData.Context!, cancellationToken);
        return result;
    }

    private async Task CompleteAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            if (_pending.Count == 0) return;
            var entries = _pending.Select(e => new AuditRecord(e.Id, e.ProjectId, e.UserId)).ToList();
            foreach (var contributor in _writers)
                await contributor.RecordAsync(entries, db.Database.GetDbConnection(),
                    db.Database.CurrentTransaction!.GetDbTransaction(), ct);
            if (_ownedTransaction is not null) await _ownedTransaction.CommitAsync(ct);
        }
        catch
        {
            if (_ownedTransaction is not null) await _ownedTransaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally { await ResetAsync(); }
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        ResetAsync().GetAwaiter().GetResult();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default) => ResetAsync();

    public override void SaveChangesCanceled(DbContextEventData eventData) =>
        ResetAsync().GetAwaiter().GetResult();

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData,
        CancellationToken cancellationToken = default) => ResetAsync();

    private async Task ResetAsync()
    {
        if (_ownedTransaction is not null) await _ownedTransaction.DisposeAsync();
        _ownedTransaction = null;
        _pending = [];
        _writers = [];
    }
}
