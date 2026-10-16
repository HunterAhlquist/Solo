using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solo.Roadmap.Data;
using Testcontainers.PostgreSql;

namespace Solo.Roadmap.Data.Tests;

/// <summary>One Postgres container for the run; each test gets a database of its own.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;

    public bool DockerAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        // An already-running server where there is no Docker, as in the host's
        // fixture (tests/Solo.Api.Testing/PostgresFixture.cs).
        var external = Environment.GetEnvironmentVariable("SOLO_TEST_POSTGRES");
        if (!string.IsNullOrEmpty(external))
        {
            _adminConnectionString = external;
            DockerAvailable = true;
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            _adminConnectionString = _container.GetConnectionString();
            DockerAvailable = true;
        }
        catch (Exception)
        {
            DockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public void SkipUnlessDockerAvailable() =>
        Skip.IfNot(DockerAvailable, "Docker is not available; these tests need it for Testcontainers.");

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"roadmap_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = name,
            MaxPoolSize = 8,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
    }

    /// <summary>
    /// A context configured the way <c>AddExtensionDbContext</c> configures it
    /// in the host: the migrations history pinned into the extension's schema.
    /// </summary>
    public static RoadmapDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<RoadmapDbContext>()
            .UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", RoadmapDbContext.Schema))
            .Options);
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

