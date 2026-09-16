using System.Data.Common;

namespace Solo.Extensibility;

/// <summary>Optional extension-owned annotations for core audit entries.</summary>
public interface ISoloAuditContributor
{
    /// <summary>Whether this request needs annotations written with its core audit entries.</summary>
    bool ShouldRecord { get; }

    /// <summary>
    /// Runs after audit ids are assigned, before the save transaction commits.
    /// Write only to your extension's schema using this connection/transaction.
    /// Do not commit or dispose either. A failure rolls back the host-owned save.
    /// </summary>
    Task RecordAsync(IReadOnlyList<AuditRecord> entries, DbConnection connection,
        DbTransaction transaction, CancellationToken ct);

    /// <summary>
    /// Labels for an already-authorized page of audit ids. Return only ids from
    /// the supplied page. The host keys your contribution by your extension id.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> ReadAsync(IReadOnlyList<long> auditIds, CancellationToken ct);
}

/// <summary>Stable audit references; no core entity or DbContext crosses the contract.</summary>
public sealed record AuditRecord(long Id, Guid? ProjectId, Guid? UserId);
