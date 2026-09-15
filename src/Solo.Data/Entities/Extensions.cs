using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>
/// One installed (or previously installed) extension. Written on every
/// successful load, and deliberately kept in CORE's schema rather than derived
/// from the packages on disk.
/// </summary>
/// <remarks>
/// <para>Without this row, deleting a package's zip and restarting leaves its
/// schema behind as an orphan nobody can attribute — data with no owner and no
/// way to offer an uninstall. With it, Admin settings can name the extension,
/// say its package is missing, and offer to drop the schema.</para>
///
/// <para><see cref="LastSeenVersion"/> also lets the host refuse a downgrade: a
/// 1.0 package over a database already migrated by 2.0 would otherwise run a
/// stale model against a newer schema and fail confusingly much later.</para>
/// </remarks>
public class ExtensionRegistration
{
    /// <summary>Manifest id. The primary key — an instance has at most one of each.</summary>
    public required string ExtensionId { get; set; }

    /// <summary>Postgres schema the extension owns, or null when it stores nothing.</summary>
    public string? SchemaName { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Manifest version at the last successful load; the downgrade guard reads this.</summary>
    public string? LastSeenVersion { get; set; }

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last successful load. A stale value with no package present is what "absent" means.</summary>
    public DateTimeOffset? LastLoadedAt { get; set; }

    public ExtensionRegistrationState State { get; set; } = ExtensionRegistrationState.Loaded;

    /// <summary>Why the extension is disabled, when it is. Surfaced in Admin settings.</summary>
    public string? FailureReason { get; set; }
}

/// <summary>What the instance currently knows about a registered extension.</summary>
public enum ExtensionRegistrationState
{
    /// <summary>Package present, loaded, serving.</summary>
    Loaded = 0,

    /// <summary>Package present but disabled — load error, bad version range, or a failed migration.</summary>
    Failed = 1,

    /// <summary>No package on disk, but data survives. The case that makes the registry worth having.</summary>
    Absent = 2,
}
