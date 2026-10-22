using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solo.DesignDocs.Data;
using Testcontainers.PostgreSql;

namespace Solo.DesignDocs.Data.Tests;

/// <summary>One Postgres container for the run; each test gets a database of its own.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool DockerAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
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
        var name = $"design_docs_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
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
    public static DesignDocsDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<DesignDocsDbContext>()
            .UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", DesignDocsDbContext.Schema))
            .Options);
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

