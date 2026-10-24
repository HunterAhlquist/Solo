using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Solo.Extensions;

namespace Solo.Api.Tests.Integration;

[Collection("postgres")]
public sealed class ExtensionMigrationTests(PostgresFixture postgres)
{
    [SkippableFact]
    public async Task First_install_and_restart_migrate_without_failed_extension_commands()
    {
        postgres.SkipUnlessDockerAvailable();
        var connectionString = await postgres.CreateDatabaseAsync("extension_migrations");
        for (var boot = 0; boot < 2; boot++)
        {
            var logs = new MigrationLogs();
            using var factory = new SoloApiFactory(connectionString, [SoloApiFactory.FixtureDirectory]);
            using var host = factory.WithWebHostBuilder(builder =>
                builder.ConfigureLogging(logging => logging.AddProvider(logs)));
            var state = host.Services.GetRequiredService<ExtensionState>();

            Assert.False(state.IsDisabled("fixture", out var reason), reason);
            Assert.DoesNotContain(logs.Entries, entry => entry.EventId == RelationalEventId.CommandError.Id
                && entry.Message.Contains("ext_fixture"));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var history = new NpgsqlCommand(
                "SELECT COUNT(*) FROM ext_fixture.\"__EFMigrationsHistory\"", connection);
            Assert.Equal(1L, await history.ExecuteScalarAsync());
        }
    }

    [SkippableFact]
    public async Task Failed_migration_logs_the_failed_command_and_disables_the_extension()
    {
        postgres.SkipUnlessDockerAvailable();
        var connectionString = await postgres.CreateDatabaseAsync("extension_migrations");
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var conflict = new NpgsqlCommand(
                "CREATE SCHEMA ext_fixture; CREATE TABLE ext_fixture.\"Milestones\" (\"Id\" uuid);", connection);
            await conflict.ExecuteNonQueryAsync();
        }

        var logs = new MigrationLogs();
        using var factory = new SoloApiFactory(connectionString, [SoloApiFactory.FixtureDirectory]);
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        var state = host.Services.GetRequiredService<ExtensionState>();

        Assert.True(state.IsDisabled("fixture", out var reason));
        Assert.Contains("migration failed", reason);
        Assert.Contains(logs.Entries, entry => entry.EventId == RelationalEventId.CommandError.Id
            && entry.Level == LogLevel.Error && entry.Message.Contains("CREATE TABLE ext_fixture.\"Milestones\""));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error
            && entry.Message.Contains("migration failed; the extension is disabled"));
    }

    private sealed class MigrationLogs : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, LogLevel Level, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Collector(this);
        public void Dispose() { }

        private sealed class Collector(MigrationLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(level)) owner.Entries.Enqueue((eventId.Id, level, formatter(state, exception)));
            }
        }
    }
}
