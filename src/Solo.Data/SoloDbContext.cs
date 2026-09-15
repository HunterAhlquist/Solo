using Microsoft.EntityFrameworkCore;
using Solo.Data.Entities;

namespace Solo.Data;

public class SoloDbContext(DbContextOptions<SoloDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<EpicGroup> EpicGroups => Set<EpicGroup>();
    public DbSet<Epic> Epics => Set<Epic>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<SubTask> SubTasks => Set<SubTask>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<InstanceSettings> InstanceSettings => Set<InstanceSettings>();
    public DbSet<ImageAsset> ImageAssets => Set<ImageAsset>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<ExtensionRegistration> ExtensionRegistrations => Set<ExtensionRegistration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var isNpgsql = Database.IsNpgsql();

        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.Issuer).HasMaxLength(500);
            e.Property(u => u.Subject).HasMaxLength(500);
            e.Property(u => u.Email).HasMaxLength(320);
            e.Property(u => u.DisplayName).HasMaxLength(300);
            e.Property(u => u.AvatarUrl).HasMaxLength(2000);
            e.HasIndex(u => new { u.Issuer, u.Subject }).IsUnique();
            e.HasIndex(u => u.Email);
        });

        modelBuilder.Entity<AuthSession>(e =>
        {
            // SHA-256, hex-encoded: fixed 64 chars. Unique — the hash is the lookup key.
            e.Property(s => s.TokenHash).HasMaxLength(64);
            e.Property(s => s.Description).HasMaxLength(200);
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => s.ExpiresAt);
            e.HasOne(s => s.User).WithMany()
                .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExtensionRegistration>(e =>
        {
            e.HasKey(x => x.ExtensionId);
            e.Property(x => x.ExtensionId).HasMaxLength(64);
            e.Property(x => x.SchemaName).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.LastSeenVersion).HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FailureReason).HasMaxLength(2000);
        });

        modelBuilder.Entity<Project>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Alias).HasMaxLength(8);
            e.Property(p => p.Icon).HasMaxLength(100);
            e.HasIndex(p => p.Alias).IsUnique();
        });

        modelBuilder.Entity<ProjectMember>(e =>
        {
            e.Property(m => m.Email).HasMaxLength(320);
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(m => new { m.ProjectId, m.Email }).IsUnique();
            e.HasIndex(m => m.UserId);
            e.HasOne(m => m.Project).WithMany(p => p.Members)
                .HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany()
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Board>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(200);
            e.Property(b => b.Alias).HasMaxLength(8);
            e.Property(b => b.Icon).HasMaxLength(100);
            e.HasIndex(b => b.ProjectId);
            // Sibling-unique: two projects may both have a "PW26" board.
            e.HasIndex(b => new { b.ProjectId, b.Alias }).IsUnique();
            e.HasOne(b => b.Project).WithMany(p => p.Boards)
                .HasForeignKey(b => b.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EpicGroup>(e =>
        {
            e.Property(g => g.Name).HasMaxLength(300);
            e.Property(g => g.Rank).HasPrecision(18, 6);
            e.HasOne(g => g.Board).WithMany(b => b.EpicGroups)
                .HasForeignKey(g => g.BoardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Epic>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Rank).HasPrecision(18, 6);
            e.HasIndex(x => x.BoardId);
            e.HasOne(x => x.Group).WithMany(g => g.Epics)
                .HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Board).WithMany(b => b.Epics)
                .HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.ToTable("Tasks");
            e.Property(t => t.Name).HasMaxLength(300);
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Rank).HasPrecision(18, 6);
            e.HasIndex(t => t.BoardId);
            e.HasIndex(t => t.EpicId);
            e.HasIndex(t => t.LabelId);
            // Serial numbers must never collide or get reassigned after a delete.
            e.HasIndex(t => new { t.BoardId, t.Number }).IsUnique();
            e.HasOne(t => t.Board).WithMany(b => b.Tasks)
                .HasForeignKey(t => t.BoardId).OnDelete(DeleteBehavior.Cascade);
            // Epic deletion detaches child tasks to ad-hoc rather than deleting them.
            e.HasOne(t => t.Epic).WithMany(x => x.Tasks)
                .HasForeignKey(t => t.EpicId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.Label).WithMany()
                .HasForeignKey(t => t.LabelId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SubTask>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(300);
            e.Property(s => s.Rank).HasPrecision(18, 6);
            e.HasIndex(s => s.TaskId);
            e.HasIndex(s => s.LabelId);
            e.HasOne(s => s.Task).WithMany(t => t.SubTasks)
                .HasForeignKey(s => s.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Label).WithMany()
                .HasForeignKey(s => s.LabelId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Label>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(100);
            e.Property(l => l.Color).HasMaxLength(9);
            e.Property(l => l.Icon).HasMaxLength(100);
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(l => new { l.ProjectId, l.BoardId });
            e.HasOne(l => l.Project).WithMany(p => p.Labels)
                .HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Board).WithMany()
                .HasForeignKey(l => l.BoardId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a default only orphans the copies; they stay as the
            // board's own labels and a reset then matches them by name.
            e.HasOne(l => l.Source).WithMany()
                .HasForeignKey(l => l.SourceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Comment>(e =>
        {
            e.HasIndex(c => c.EpicId);
            e.HasIndex(c => c.TaskId);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_Comments_ExactlyOneParent",
                "(\"EpicId\" IS NULL) <> (\"TaskId\" IS NULL)"));
            e.HasOne(c => c.Author).WithMany()
                .HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.Epic).WithMany(x => x.Comments)
                .HasForeignKey(c => c.EpicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Task).WithMany(t => t.Comments)
                .HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Assignment>(e =>
        {
            e.HasIndex(a => a.EpicId);
            e.HasIndex(a => a.TaskId);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_Assignments_ExactlyOneParent",
                "(\"EpicId\" IS NULL) <> (\"TaskId\" IS NULL)"));
            var userIdx = e.HasIndex(a => new { a.UserId, a.EpicId, a.TaskId }).IsUnique();
            if (isNpgsql)
            {
                userIdx.AreNullsDistinct(false);
            }
            e.HasOne(a => a.User).WithMany()
                .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Epic).WithMany(x => x.Assignments)
                .HasForeignKey(a => a.EpicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Task).WithMany(t => t.Assignments)
                .HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.Property(a => a.TableName).HasMaxLength(100);
            e.Property(a => a.RowId).HasMaxLength(100);
            e.Property(a => a.ColumnName).HasMaxLength(100);
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => new { a.ProjectId, a.Timestamp });
        });

        modelBuilder.Entity<InstanceSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.ThemeId).HasMaxLength(64);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_InstanceSettings_Singleton", $"\"Id\" = {Entities.InstanceSettings.SingletonId}"));
        });

        modelBuilder.Entity<ImageAsset>(e =>
        {
            e.Property(i => i.ContentType).HasMaxLength(100);
        });

        // Per-extension field bags. jsonb on Postgres; the unit-test SQLite
        // provider has no such type and keeps them as plain text, which is all
        // the store needs since it only ever round-trips whole documents.
        if (isNpgsql)
        {
            modelBuilder.Entity<TaskItem>().Property(t => t.Extensions).HasColumnType("jsonb");
            modelBuilder.Entity<Epic>().Property(e => e.Extensions).HasColumnType("jsonb");
            modelBuilder.Entity<Board>().Property(b => b.Extensions).HasColumnType("jsonb");
        }

        // xmin optimistic-concurrency token on all editable entities (Postgres only;
        // other providers — SQLite in unit tests — keep Version as a plain column).
        if (isNpgsql)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                         .Where(t => typeof(IVersioned).IsAssignableFrom(t.ClrType)))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .Property(nameof(IVersioned.Version))
                    .IsRowVersion();
            }
        }
    }
}
