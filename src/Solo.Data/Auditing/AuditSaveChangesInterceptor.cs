using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Solo.Data.Entities;

namespace Solo.Data.Auditing;

/// <summary>
/// Writes one <see cref="AuditEntry"/> per changed column for every modified or
/// deleted entity, plus a single creation row (column and previous value null)
/// for added entities. Rows are appended to the same context so they persist in
/// the same transaction as the change. <see cref="AuditEntry"/> itself is
/// excluded from auditing.
/// </summary>
public sealed class AuditSaveChangesInterceptor(ICurrentUserProvider currentUser) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            WriteAuditEntries(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            WriteAuditEntries(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void WriteAuditEntries(DbContext context)
    {
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.Entity is not AuditEntry &&
                        e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var projectIds = ProjectScopeResolver.Resolve(context, entries.Select(e => e.Entity));
        var timestamp = DateTime.UtcNow;
        var userId = currentUser.UserId;
        var audits = new List<AuditEntry>();

        foreach (var entry in entries)
        {
            var tableName = entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name;
            var rowId = FormatValue(entry.Properties.First(p => p.Metadata.IsPrimaryKey()).CurrentValue)
                        ?? string.Empty;
            projectIds.TryGetValue(entry.Entity, out var projectId);

            switch (entry.State)
            {
                case EntityState.Added:
                    audits.Add(new AuditEntry
                    {
                        Timestamp = timestamp,
                        UserId = userId,
                        ProjectId = projectId,
                        TableName = tableName,
                        RowId = rowId,
                        ColumnName = null,
                        PreviousValue = null,
                    });
                    break;

                case EntityState.Modified:
                    foreach (var property in AuditableProperties(entry).Where(p => p.IsModified))
                    {
                        audits.Add(new AuditEntry
                        {
                            Timestamp = timestamp,
                            UserId = userId,
                            ProjectId = projectId,
                            TableName = tableName,
                            RowId = rowId,
                            ColumnName = property.Metadata.Name,
                            PreviousValue = FormatValue(property.OriginalValue),
                        });
                    }

                    break;

                case EntityState.Deleted:
                    foreach (var property in AuditableProperties(entry))
                    {
                        audits.Add(new AuditEntry
                        {
                            Timestamp = timestamp,
                            UserId = userId,
                            ProjectId = projectId,
                            TableName = tableName,
                            RowId = rowId,
                            ColumnName = property.Metadata.Name,
                            PreviousValue = FormatValue(property.OriginalValue),
                        });
                    }

                    break;
            }
        }

        if (audits.Count > 0)
        {
            context.Set<AuditEntry>().AddRange(audits);
        }
    }

    private static IEnumerable<PropertyEntry> AuditableProperties(EntityEntry entry) =>
        entry.Properties.Where(p =>
            !p.Metadata.IsPrimaryKey() &&
            !p.Metadata.IsConcurrencyToken &&
            p.Metadata.Name != nameof(IVersioned.Version));

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
