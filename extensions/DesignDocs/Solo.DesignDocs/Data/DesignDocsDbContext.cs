using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;

namespace Solo.DesignDocs.Data;

/// <summary>
/// The extension's own store, in its own Postgres schema, with its own
/// migration history.
/// </summary>
/// <remarks>
/// <para>Core rows (projects, users) are referenced by bare Guid with no foreign
/// key into core's schema, as in Roadmap. Inside the schema, deleting a vault
/// cascades to everything in it.</para>
///
/// <para>The update log points at its vault rather than at its note or sketch:
/// one log serves both kinds of document, so a foreign key to either would be
/// wrong for the other. Deleting a note or folder removes its documents' log
/// rows explicitly, in the same transaction (see <c>NotesController</c>).</para>
/// </remarks>
public sealed class DesignDocsDbContext(DbContextOptions<DesignDocsDbContext> options) : DbContext(options)
{
    /// <summary>Must match the manifest's <c>schema</c>; <see cref="DesignDocsExtension"/> checks that it does.</summary>
    public const string Schema = "ext_design_docs";

    public DbSet<Vault> Vaults => Set<Vault>();

    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<Note> Notes => Set<Note>();

    public DbSet<Sketch> Sketches => Set<Sketch>();

    public DbSet<DocUpdate> DocUpdates => Set<DocUpdate>();

    public DbSet<VaultImage> Images => Set<VaultImage>();

    public DbSet<MemberColor> MemberColors => Set<MemberColor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Vault>(e =>
        {
            e.ToTable("Vaults");
            e.Property(v => v.Name).HasMaxLength(DocsLimits.Name);
            e.HasIndex(v => new { v.ProjectId, v.Name }).IsUnique();
            e.Property(v => v.Version).IsRowVersion();
        });

        modelBuilder.Entity<Folder>(e =>
        {
            e.ToTable("Folders");
            e.Property(f => f.Name).HasMaxLength(DocsLimits.Name);
            // A root folder has no parent; two roots with one name still clash.
            e.HasIndex(f => new { f.VaultId, f.ParentId, f.Name }).IsUnique().AreNullsDistinct(false);
            e.HasOne<Vault>().WithMany().HasForeignKey(f => f.VaultId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Folder>().WithMany().HasForeignKey(f => f.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.Property(f => f.Version).IsRowVersion();
        });

        modelBuilder.Entity<Note>(e =>
        {
            e.ToTable("Notes");
            e.Property(n => n.Name).HasMaxLength(DocsLimits.Name);
            e.Property(n => n.Headings).HasColumnType("jsonb");
            e.HasIndex(n => new { n.VaultId, n.FolderId, n.Name }).IsUnique().AreNullsDistinct(false);
            e.HasOne<Vault>().WithMany().HasForeignKey(n => n.VaultId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Folder>().WithMany().HasForeignKey(n => n.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.Property(n => n.Version).IsRowVersion();
        });

        modelBuilder.Entity<Sketch>(e =>
        {
            e.ToTable("Sketches");
            e.Property(s => s.Name).HasMaxLength(DocsLimits.Name);
            e.Property(s => s.PageSize).HasMaxLength(16);
            e.Property(s => s.Orientation).HasMaxLength(16);
            e.Property(s => s.Pattern).HasMaxLength(16);
            // Embeds name a sketch by its name alone, so the name is the vault's to give once.
            e.HasIndex(s => new { s.VaultId, s.Name }).IsUnique();
            e.HasIndex(s => s.NoteId);
            e.HasOne<Vault>().WithMany().HasForeignKey(s => s.VaultId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Note>().WithMany().HasForeignKey(s => s.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.Version).IsRowVersion();
        });

        modelBuilder.Entity<DocUpdate>(e =>
        {
            e.ToTable("DocUpdates");
            e.HasKey(u => u.Seq);
            // BY DEFAULT rather than ALWAYS: compaction writes a snapshot under
            // the seq it replaces, so readers that saw that seq stay in step.
            e.Property(u => u.Seq).UseIdentityByDefaultColumn();
            e.HasIndex(u => new { u.DocumentId, u.Seq });
            e.HasOne<Vault>().WithMany().HasForeignKey(u => u.VaultId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VaultImage>(e =>
        {
            e.ToTable("Images");
            e.Property(i => i.Path).HasMaxLength(DocsLimits.Path);
            e.Property(i => i.ContentType).HasMaxLength(32);
            e.HasIndex(i => new { i.VaultId, i.Path }).IsUnique();
            e.HasOne<Vault>().WithMany().HasForeignKey(i => i.VaultId).OnDelete(DeleteBehavior.Cascade);
            e.Property(i => i.Version).IsRowVersion();
        });

        modelBuilder.Entity<MemberColor>(e =>
        {
            e.ToTable("MemberColors");
            e.HasKey(c => new { c.ProjectId, c.UserId });
            e.HasIndex(c => new { c.ProjectId, c.Slot }).IsUnique();
        });
    }
}

/// <summary>A design doc: one Obsidian-style vault of folders and notes, in one project.</summary>
public sealed class Vault
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Core project, by bare id.</summary>
    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    /// <summary>A closed vault is read-only until reopened, and only a closed one can be deleted.</summary>
    public bool IsClosed { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Core user, by bare id.</summary>
    public Guid CreatedBy { get; set; }

    public uint Version { get; set; }

    public VaultDto ToDto() => new(Id, ProjectId, Name, IsClosed, ClosedAt, CreatedAt, Version);
}

public sealed class Folder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid VaultId { get; set; }

    /// <summary>The folder it sits in; null at the vault's root.</summary>
    public Guid? ParentId { get; set; }

    public required string Name { get; set; }

    public uint Version { get; set; }

    public FolderDto ToDto() => new(Id, ParentId, Name, Version);
}

/// <summary>A markdown note. Its id is what links carry, so it never changes on a rename or move.</summary>
public sealed class Note
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid VaultId { get; set; }

    public Guid? FolderId { get; set; }

    /// <summary>The file name, without <c>.md</c>.</summary>
    public required string Name { get; set; }

    /// <summary>The last copy a writer sent. Can lag the live document, which is the truth.</summary>
    public string Markdown { get; set; } = "";

    /// <summary>The newest update <see cref="Markdown"/> includes; an older copy is ignored.</summary>
    public long MarkdownSeq { get; set; }

    /// <summary>A JSON array of <see cref="NoteHeadingDto"/>, as the writer's client worked them out.</summary>
    public string Headings { get; set; } = "[]";

    /// <summary>
    /// Imported text no client has turned into a live document yet. Cleared by
    /// the first stored update, which is the seeding client's conversion of it.
    /// </summary>
    public string? SeedMarkdown { get; set; }

    public Guid? LastEditedBy { get; set; }

    public DateTimeOffset? LastEditedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public uint Version { get; set; }

    public NoteSummaryDto ToSummary() => new(Id, FolderId, Name, LastEditedBy, LastEditedAt, Version);
}

/// <summary>A pixel sketch, embedded in one note as <c>![[Name.sketch]]</c>.</summary>
public sealed class Sketch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid VaultId { get; set; }

    public Guid NoteId { get; set; }

    public required string Name { get; set; }

    public string PageSize { get; set; } = SketchPage.A4;

    public string Orientation { get; set; } = SketchPage.Portrait;

    public string Pattern { get; set; } = SketchPage.Empty;

    public Guid? LastEditedBy { get; set; }

    public DateTimeOffset? LastEditedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public uint Version { get; set; }

    public SketchSummaryDto ToSummary() => new(Id, NoteId, Name, PageSize, Orientation, Pattern, Version);
}

/// <summary>
/// One entry in a document's log: an opaque Yjs update as a client sent it, or
/// a snapshot that replaced every entry up to its own <see cref="Seq"/>.
/// </summary>
public sealed class DocUpdate
{
    public long Seq { get; set; }

    /// <summary>The note or sketch.</summary>
    public Guid DocumentId { get; set; }

    public Guid VaultId { get; set; }

    public bool IsSnapshot { get; set; }

    public required byte[] Data { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An image file in a vault, at its vault-relative path, as Obsidian keeps attachments.</summary>
public sealed class VaultImage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid VaultId { get; set; }

    public required string Path { get; set; }

    public required string ContentType { get; set; }

    public required byte[] Data { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public long Bytes { get; set; }

    public Guid UploadedBy { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    public uint Version { get; set; }

    public ImageSummaryDto ToSummary() => new(Id, Path, ContentType, Width, Height, Bytes, Version);
}

/// <summary>A member's color slot in a project, given in the order people first opened a doc.</summary>
public sealed class MemberColor
{
    public Guid ProjectId { get; set; }

    public Guid UserId { get; set; }

    public int Slot { get; set; }
}
