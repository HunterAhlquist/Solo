using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solo.Data;

/// <summary>Used only by the dotnet-ef tooling to generate migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SoloDbContext>
{
    public SoloDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SoloDbContext>()
            .UseNpgsql("Host=localhost;Database=solo;Username=solo;Password=design-time-only")
            .Options;
        return new SoloDbContext(options);
    }
}
