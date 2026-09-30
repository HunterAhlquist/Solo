using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solo.Roadmap.Data;

/// <summary>
/// Used only by the dotnet-ef tooling to scaffold this extension's migrations,
/// which the host applies at startup — it never generates them itself.
/// </summary>
public sealed class RoadmapDesignTimeFactory : IDesignTimeDbContextFactory<RoadmapDbContext>
{
    public RoadmapDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RoadmapDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=solo;Username=solo;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", RoadmapDbContext.Schema))
            .Options);
}
