using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SoloExtension_CSharp.Data;

/// <summary>
/// What <c>dotnet ef</c> builds the context from. It never connects: scaffolding
/// a migration only reads the model, so the connection string is a placeholder.
/// At runtime the host supplies the real one through
/// <c>AddExtensionDbContext</c>.
/// </summary>
public sealed class StarterDesignTimeFactory : IDesignTimeDbContextFactory<StarterDbContext>
{
    public StarterDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<StarterDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=solo;Username=solo;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", StarterDbContext.Schema))
            .Options);
}
