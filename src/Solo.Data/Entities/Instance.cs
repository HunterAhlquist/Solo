using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>
/// An uploaded raster or vector image, stored inline in Postgres. Used as the
/// custom icon for a project, board or label. Immutable once created — the id
/// is the cache key, so content never changes under a given id.
/// </summary>
public class ImageAsset
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One row per changed column, written by the SaveChanges interceptor in the
/// same transaction as the change. Stores only the previous value: history,
/// not state. <see cref="ProjectId"/> is denormalized so per-project audit
/// queries survive entity deletion.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public required string TableName { get; set; }
    public required string RowId { get; set; }
    /// <summary>Null on row creation events.</summary>
    public string? ColumnName { get; set; }
    public string? PreviousValue { get; set; }
}

/// <summary>Single-row config table (Id fixed at 1).</summary>
public class InstanceSettings : IVersioned
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public int AuditRetentionDays { get; set; } = 90;
    /// <summary>Once true, the static setup account is permanently disabled.</summary>
    public bool SetupCompleted { get; set; }
    /// <summary>
    /// Site-wide theme id (see the theme catalog). Null means the built-in
    /// default; so does a value naming a theme that is no longer installed —
    /// removing a theme package must not leave the instance unrenderable.
    /// </summary>
    public string? ThemeId { get; set; }
    /// <summary>
    /// What this instance calls itself: the name in the app bar, on the
    /// sign-in page and in page titles, and what the desktop app lists it
    /// under. Null means it has none of its own and goes by "Solo".
    /// </summary>
    public string? DisplayName { get; set; }
    /// <summary>
    /// The line under the name on the sign-in page. Null means the instance
    /// has none of its own and shows the product's.
    /// </summary>
    public string? Tagline { get; set; }
    public uint Version { get; set; }
}
