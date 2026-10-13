using Microsoft.EntityFrameworkCore;

namespace SoloExtension_CSharp.Data;

/// <summary>
/// The extension's own context, in its own Postgres schema. Core's context is
/// not available to an extension: read core data through <c>ISoloQuery</c>,
/// which only returns what the caller may see.
/// </summary>
public sealed class StarterDbContext(DbContextOptions<StarterDbContext> options) : DbContext(options)
{
    /// <summary>
    /// "ext_" and the extension's id, with hyphens as underscores. The host
    /// derives the same name and refuses a manifest that says anything else.
    /// </summary>
    public const string Schema = "ext_solo_extension_id";

    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Note>(note =>
        {
            note.Property(n => n.Text).HasMaxLength(2000);
            note.HasIndex(n => new { n.ProjectId, n.CreatedAt });
        });
    }
}
