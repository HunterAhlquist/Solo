using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solo.DesignDocs.Data;

/// <summary>
/// Used only by the dotnet-ef tooling to scaffold this extension's migrations,
/// which the host applies at startup. It never generates them itself.
/// </summary>
public sealed class DesignDocsDesignTimeFactory : IDesignTimeDbContextFactory<DesignDocsDbContext>
{
    public DesignDocsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DesignDocsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=solo;Username=solo;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", DesignDocsDbContext.Schema))
            .Options);
}
