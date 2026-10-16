using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Solo.Shared.Dtos;
using Testcontainers.PostgreSql;
using Xunit;

namespace Solo.Api.Testing;

/// <summary>
/// One Postgres container for the whole test run; each test class gets its own
/// database on it so state (e.g. the one-shot setup handoff) never leaks
/// between classes.
/// </summary>
/// <remarks>
/// xunit only honours a collection definition in the assembly whose tests use
/// it, so each test project declares the <c>"postgres"</c> collection itself.
/// </remarks>
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
        Skip.IfNot(DockerAvailable, "Docker is not available; integration tests need it for Testcontainers.");

    /// <summary>
    /// A fresh database per call: xunit instantiates the test class (and thus
    /// runs InitializeAsync) once per test, so names must never repeat.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        var adminConnectionString = _container!.GetConnectionString();
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync();

        // One shared container serves the whole run, and every test builds its own
        // host against its own database — so every test leaves behind a
        // connection pool that EF's static provider cache keeps alive for the
        // rest of the process. At the defaults (100 connections a pool, idle
        // ones held for five minutes) a long run exhausts the server's
        // max_connections, and the failure lands on whichever unrelated test
        // runs next: "sorry, too many clients already".
        //
        // So: a small ceiling, and idle connections handed back within seconds
        // rather than minutes. A single test never needs more than a handful.
        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = name,
            MaxPoolSize = 8,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
    }
}
