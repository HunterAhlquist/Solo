using Microsoft.EntityFrameworkCore;
using Solo.Roadmap.Contracts;

namespace Solo.Roadmap.Data;

/// <summary>
/// The extension's own store, in its own Postgres schema, with its own
/// migration history.
/// </summary>
/// <remarks>
/// Core rows — projects and tasks — are referenced by bare Guid, with no foreign
/// key crossing into core's schema: an FK would stop core from ever changing its
/// own tables, and would make core's deletes depend on this package. Every read
/// tolerates a task that has since gone (see <c>RoadmapsController</c>'s prune
/// and <see cref="TaskEventsHandler"/>).
/// </remarks>
public sealed class RoadmapDbContext(DbContextOptions<RoadmapDbContext> options) : DbContext(options)
{
    /// <summary>Must match the manifest's <c>schema</c>; <see cref="RoadmapExtension"/> checks that it does.</summary>
    public const string Schema = "ext_roadmap";

    public DbSet<Roadmap> Roadmaps => Set<Roadmap>();

    public DbSet<RoadmapEntry> Entries => Set<RoadmapEntry>();

    public DbSet<RoadmapMilestone> Milestones => Set<RoadmapMilestone>();

    public DbSet<RoadmapSettings> Settings => Set<RoadmapSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Roadmap>(e =>
        {
            e.ToTable("Roadmaps");
            e.Property(r => r.Title).HasMaxLength(RoadmapLimits.Title);
            e.HasIndex(r => r.ProjectId);
            // xmin, like core's Version: a rename carries it and a stale one is a 409.
            e.Property(r => r.Version).IsRowVersion();
        });

        modelBuilder.Entity<RoadmapEntry>(e =>
        {
            e.ToTable("Entries");
            e.Property(x => x.Rank).HasPrecision(18, 6);
            // A task appears on a roadmap at most once.
            e.HasIndex(x => new { x.RoadmapId, x.TaskId }).IsUnique();
            // The task view asks which roadmaps hold a task; the delete handler too.
            e.HasIndex(x => x.TaskId);
            e.HasOne(x => x.Roadmap).WithMany(r => r.Entries)
                .HasForeignKey(x => x.RoadmapId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Version).IsRowVersion();
        });

        modelBuilder.Entity<RoadmapMilestone>(e =>
        {
            e.ToTable("Milestones");
            e.Property(m => m.Name).HasMaxLength(RoadmapLimits.MilestoneName);
            e.Property(m => m.Color).HasMaxLength(7);
            e.HasIndex(m => m.RoadmapId);
            e.HasOne(m => m.Roadmap).WithMany(r => r.Milestones)
                .HasForeignKey(m => m.RoadmapId).OnDelete(DeleteBehavior.Cascade);
            e.Property(m => m.Version).IsRowVersion();
        });

        modelBuilder.Entity<RoadmapSettings>(e =>
        {
            e.ToTable("Settings");
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Version).IsRowVersion();
        });
    }
}

/// <summary>A named timeline of tasks, belonging to one project.</summary>
public sealed class Roadmap
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Core project, by bare id.</summary>
    public Guid ProjectId { get; set; }

    public required string Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Core user, by bare id.</summary>
    public Guid CreatedByUserId { get; set; }

    public uint Version { get; set; }

    public List<RoadmapEntry> Entries { get; set; } = [];

    public List<RoadmapMilestone> Milestones { get; set; } = [];
}

/// <summary>
/// One task's place on one roadmap. Its order here is the roadmap's own and has
/// nothing to do with the task's rank on its board.
/// </summary>
public sealed class RoadmapEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RoadmapId { get; set; }

    public Roadmap Roadmap { get; set; } = null!;

    /// <summary>Core task, by bare id. It may have been deleted since.</summary>
    public Guid TaskId { get; set; }

    public decimal Rank { get; set; }

    /// <summary>The day the task was added on. A task with no deadline is drawn here.</summary>
    public DateOnly AnchorDay { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public uint Version { get; set; }
}

/// <summary>A named deadline on one roadmap, independent of any task on it.</summary>
public sealed class RoadmapMilestone
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RoadmapId { get; set; }

    public Roadmap Roadmap { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>The deadline day; the line is drawn at its end.</summary>
    public DateOnly Day { get; set; }

    /// <summary><c>#rrggbb</c>, as <see cref="RoadmapLimits.ColorPattern"/> allows.</summary>
    public string Color { get; set; } = RoadmapLimits.DefaultMilestoneColor;

    public uint Version { get; set; }

    public RoadmapMilestoneDto ToDto() => new(Id, Name, Day, Color, Version);
}

/// <summary>The timeline's instance-wide settings: one row, created on first read with the defaults.</summary>
public sealed class RoadmapSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public int PastDays { get; set; } = RoadmapLimits.DefaultPastDays;

    public int FutureDays { get; set; } = RoadmapLimits.DefaultFutureDays;

    public DayOfWeek SprintStartDay { get; set; } = RoadmapLimits.DefaultSprintStartDay;

    public int SprintLengthWeeks { get; set; } = RoadmapLimits.DefaultSprintWeeks;

    public uint Version { get; set; }

    public RoadmapSettingsDto ToDto() => new(PastDays, FutureDays, SprintStartDay, SprintLengthWeeks, Version);
}
