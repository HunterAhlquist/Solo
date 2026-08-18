using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solo.Extension.Fixture;

public sealed class Milestone
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    /// <summary>Core task this milestone points at — a bare Guid, deliberately with no FK.</summary>
    public Guid? TaskId { get; set; }
}

/// <summary>
/// The extension's own context. <c>HasDefaultSchema</c> must match the
/// manifest's schema; the host pins the migrations history table into the same
/// schema when it registers this.
/// </summary>
public sealed class FixtureDbContext(DbContextOptions<FixtureDbContext> options) : DbContext(options)
{
    public DbSet<Milestone> Milestones => Set<Milestone>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ext_fixture");
        modelBuilder.Entity<Milestone>().Property(m => m.Name).HasMaxLength(200);
    }
}

/// <summary>
/// Needed by <c>dotnet ef migrations add</c> only — migrations are scaffolded at
/// build time exactly as core's are, never generated at runtime.
/// </summary>
public sealed class FixtureDesignTimeFactory : IDesignTimeDbContextFactory<FixtureDbContext>
{
    public FixtureDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FixtureDbContext>()
            .UseNpgsql("Host=localhost;Database=solo;Username=solo;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "ext_fixture"))
            .Options);
}
