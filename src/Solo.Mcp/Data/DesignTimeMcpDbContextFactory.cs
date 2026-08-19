using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solo.Mcp.Data;

/// <summary>
/// Used only by the dotnet-ef tooling to scaffold this extension's migrations.
/// </summary>
/// <remarks>
/// An extension needs its own, mirroring core's: migrations cannot be generated
/// at runtime, so the author scaffolds them at build time and the host only
/// applies what the package ships.
/// </remarks>
public sealed class DesignTimeMcpDbContextFactory : IDesignTimeDbContextFactory<McpDbContext>
{
    public McpDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<McpDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=solo;Username=solo;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", McpDbContext.Schema))
            .Options;
        return new McpDbContext(options);
    }
}
