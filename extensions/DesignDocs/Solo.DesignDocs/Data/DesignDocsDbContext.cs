using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solo.DesignDocs.Data;

public sealed class DesignDocsDbContext(DbContextOptions<DesignDocsDbContext> options) : DbContext(options)
{
    public const string Schema = "ext_design_docs";
    protected override void OnModelCreating(ModelBuilder b) => b.HasDefaultSchema(Schema);
}

public sealed class DesignDocsDesignTimeFactory : IDesignTimeDbContextFactory<DesignDocsDbContext>
{
    public DesignDocsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DesignDocsDbContext>()
        .UseNpgsql("Host=localhost;Database=solo;Username=solo;Password=design-time-only",
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DesignDocsDbContext.Schema)).Options);
}
