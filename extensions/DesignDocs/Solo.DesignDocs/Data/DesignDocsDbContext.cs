using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solo.DesignDocs.Data;

public sealed class DesignDocsDbContext(DbContextOptions<DesignDocsDbContext> options) : DbContext(options)
{
    public const string Schema = "ext_design_docs";
    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Sketch> Sketches => Set<Sketch>();
    public DbSet<DocUpdate> DocUpdates => Set<DocUpdate>();
    public DbSet<DocImage> Images => Set<DocImage>();
    public DbSet<MemberColor> MemberColors => Set<MemberColor>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Vault>(e => {
            e.ToTable("Vaults"); e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique(); e.HasIndex(x => x.ProjectId);
            e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<Folder>(e => {
            e.ToTable("Folders"); e.Property(x => x.Name).HasMaxLength(200);
            e.HasAlternateKey(x => new { x.Id, x.VaultId });
            e.HasOne<Vault>().WithMany().HasForeignKey(x => x.VaultId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => new { x.ParentId, x.VaultId })
                .HasPrincipalKey(x => new { x.Id, x.VaultId }).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.VaultId, x.ParentId, x.Name }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<Note>(e => {
            e.ToTable("Notes"); e.Property(x => x.Name).HasMaxLength(200);
            e.HasAlternateKey(x => new { x.Id, x.VaultId });
            e.HasOne<Vault>().WithMany().HasForeignKey(x => x.VaultId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => new { x.FolderId, x.VaultId })
                .HasPrincipalKey(x => new { x.Id, x.VaultId }).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.VaultId, x.FolderId, x.Name }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.Headings).HasColumnType("jsonb"); e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<Sketch>(e => {
            e.ToTable("Sketches"); e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne<Note>().WithMany().HasForeignKey(x => new { x.NoteId, x.VaultId })
                .HasPrincipalKey(x => new { x.Id, x.VaultId }).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.NoteId, x.Name }).IsUnique(); e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<DocUpdate>(e => {
            e.ToTable("DocUpdates", t => t.HasCheckConstraint("CK_DocUpdates_Document",
                "(\"NoteId\" IS NOT NULL AND \"SketchId\" IS NULL AND \"DocumentId\" = \"NoteId\") OR (\"NoteId\" IS NULL AND \"SketchId\" IS NOT NULL AND \"DocumentId\" = \"SketchId\")"));
            e.HasKey(x => x.Seq); e.Property(x => x.Seq).UseIdentityAlwaysColumn();
            e.HasIndex(x => new { x.DocumentId, x.Seq });
            e.HasIndex(x => new { x.DocumentId, x.ClientUpdateId }).IsUnique();
            // The exclusive FKs let the database cascade a polymorphic log without core references.
            e.HasOne<Note>().WithMany().HasForeignKey(x => x.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Sketch>().WithMany().HasForeignKey(x => x.SketchId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DocImage>(e => {
            e.ToTable("Images"); e.Property(x => x.Path).HasMaxLength(1024);
            e.HasOne<Vault>().WithMany().HasForeignKey(x => x.VaultId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.VaultId, x.Path }).IsUnique(); e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<MemberColor>(e => {
            e.ToTable("MemberColors"); e.HasKey(x => new { x.ProjectId, x.UserId });
            e.HasIndex(x => new { x.ProjectId, x.Slot }).IsUnique();
        });
    }
}

public sealed class Vault
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public bool IsClosed { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; set; }
    public uint Version { get; set; }
}
public sealed class Folder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid VaultId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public uint Version { get; set; }
}
public sealed class Note
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid VaultId { get; set; }
    public Guid? FolderId { get; set; }
    public string Name { get; set; } = "";
    public string Markdown { get; set; } = "";
    public long MarkdownSeq { get; set; }
    public string Headings { get; set; } = "[]";
    public string? SeedMarkdown { get; set; }
    public Guid? SeededByToken { get; set; }
    public Guid? LastEditedBy { get; set; }
    public DateTimeOffset? LastEditedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }
}
public sealed class Sketch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid NoteId { get; set; }
    public Guid VaultId { get; set; }
    public string Name { get; set; } = "";
    public string PageSize { get; set; } = "A4";
    public string Orientation { get; set; } = "portrait";
    public string Pattern { get; set; } = "empty";
    public Guid? LastEditedBy { get; set; }
    public DateTimeOffset? LastEditedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }
}
public sealed class DocUpdate
{
    public long Seq { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? NoteId { get; set; }
    public Guid? SketchId { get; set; }
    public Guid? ClientUpdateId { get; set; }
    public bool IsSnapshot { get; set; }
    // A compacted row receives a new sequence identity, but covers only this prefix.
    public long ThroughSeq { get; set; }
    public byte[] Data { get; set; } = [];
    public Guid UserId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class DocImage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid VaultId { get; set; }
    public string Path { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bytes { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }
}
public sealed class MemberColor
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public int Slot { get; set; }
}
public sealed class DesignDocsDesignTimeFactory : IDesignTimeDbContextFactory<DesignDocsDbContext>
{
    public DesignDocsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DesignDocsDbContext>()
        .UseNpgsql("Host=localhost;Database=solo;Username=solo;Password=design-time-only",
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DesignDocsDbContext.Schema)).Options);
}
