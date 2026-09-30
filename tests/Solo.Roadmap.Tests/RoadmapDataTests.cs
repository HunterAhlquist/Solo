using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solo.Extensibility;
using Solo.Roadmap.Data;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Roadmap.Tests;

/// <summary>The extension's schema, constraints, delete handling and settings row, against a real Postgres.</summary>
[Collection("postgres")]
public sealed class RoadmapDataTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = null!;
    private RoadmapDbContext _db = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync();
        _db = PostgresFixture.Context(_connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task The_migration_only_touches_ext_roadmap()
    {
        postgres.SkipUnlessDockerAvailable();
        var before = await TablesAsync();

        await _db.Database.MigrateAsync();

        var added = (await TablesAsync()).Except(before).ToList();
        Assert.All(added, table => Assert.StartsWith("ext_roadmap.", table));
        Assert.Equal(
            ["ext_roadmap.Entries", "ext_roadmap.Roadmaps", "ext_roadmap.Settings", "ext_roadmap.__EFMigrationsHistory"],
            added.Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task A_task_appears_on_a_roadmap_at_most_once()
    {
        postgres.SkipUnlessDockerAvailable();
        await _db.Database.MigrateAsync();
        var roadmap = await RoadmapAsync();
        var taskId = Guid.NewGuid();
        _db.Entries.Add(Entry(roadmap, taskId, 1024m));
        await _db.SaveChangesAsync();

        _db.Entries.Add(Entry(roadmap, taskId, 2048m));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);

        // The same task on a second roadmap is fine.
        _db.ChangeTracker.Clear();
        _db.Entries.Add(Entry(await RoadmapAsync(), taskId, 1024m));
        await _db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Deleting_a_roadmap_deletes_its_entries()
    {
        postgres.SkipUnlessDockerAvailable();
        await _db.Database.MigrateAsync();
        var roadmap = await RoadmapAsync();
        _db.Entries.Add(Entry(roadmap, Guid.NewGuid(), 1024m));
        await _db.SaveChangesAsync();

        await _db.Roadmaps.Where(r => r.Id == roadmap.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await _db.Entries.CountAsync());
    }

    [SkippableFact]
    public async Task Deleting_a_task_removes_its_entries_through_the_handler()
    {
        postgres.SkipUnlessDockerAvailable();
        await _db.Database.MigrateAsync();
        var first = await RoadmapAsync();
        var second = await RoadmapAsync();
        var doomed = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        _db.Entries.AddRange(Entry(first, doomed, 1024m), Entry(second, doomed, 1024m), Entry(first, survivor, 2048m));
        await _db.SaveChangesAsync();

        var handler = new TaskDeletedHandler(_db, new StubQuery(task: null));
        await handler.HandleAsync(Deleted(doomed), CancellationToken.None);

        Assert.Equal([survivor], await _db.Entries.Select(e => e.TaskId).ToListAsync());
    }

    /// <summary>
    /// A board move raises a delete on the board the task left; it is still in
    /// the project and still findable, so its entries stay.
    /// </summary>
    [SkippableFact]
    public async Task A_task_that_is_still_readable_keeps_its_entries()
    {
        postgres.SkipUnlessDockerAvailable();
        await _db.Database.MigrateAsync();
        var roadmap = await RoadmapAsync();
        var moved = Guid.NewGuid();
        _db.Entries.Add(Entry(roadmap, moved, 1024m));
        await _db.SaveChangesAsync();

        var stillThere = new TaskDto(moved, Guid.NewGuid(), null, 3, "Moved", null, null, TaskState.Unstarted,
            DateTime.UtcNow, null, 1024m, 1, [], []);
        await new TaskDeletedHandler(_db, new StubQuery(stillThere)).HandleAsync(Deleted(moved), CancellationToken.None);
        await new TaskDeletedHandler(_db, new StubQuery(task: null)).HandleAsync(
            Deleted(moved) with { Kind = ChangeKind.Updated }, CancellationToken.None);

        Assert.Equal(1, await _db.Entries.CountAsync());
    }

    [SkippableFact]
    public async Task The_settings_row_is_created_on_first_read_with_the_defaults()
    {
        postgres.SkipUnlessDockerAvailable();
        await _db.Database.MigrateAsync();
        var store = new RoadmapSettingsStore(_db, new FixedClock(new DateTimeOffset(2027, 6, 15, 12, 0, 0, TimeSpan.Zero)));

        var first = await store.GetAsync();

        Assert.Equal((365, 730, 14), (first.PastDays, first.FutureDays, first.StripeIntervalDays));
        Assert.Equal(new DateOnly(2027, 1, 1), first.StripeAnchor);

        // Read again from a fresh context — no second row, and the stored anchor
        // doesn't follow the clock into a later year.
        await using var later = PostgresFixture.Context(_connectionString);
        var reread = await new RoadmapSettingsStore(
            later, new FixedClock(new DateTimeOffset(2029, 2, 1, 0, 0, 0, TimeSpan.Zero))).GetAsync();
        Assert.Equal(new DateOnly(2027, 1, 1), reread.StripeAnchor);
        Assert.Equal(first.Version, reread.Version);
        Assert.Equal(1, await later.Settings.CountAsync());
    }

    private async Task<Data.Roadmap> RoadmapAsync()
    {
        var roadmap = new Data.Roadmap { ProjectId = Guid.NewGuid(), Title = "Plan", CreatedByUserId = Guid.NewGuid() };
        _db.Roadmaps.Add(roadmap);
        await _db.SaveChangesAsync();
        return roadmap;
    }

    private static RoadmapEntry Entry(Data.Roadmap roadmap, Guid taskId, decimal rank) =>
        new() { RoadmapId = roadmap.Id, TaskId = taskId, Rank = rank, AnchorDay = new DateOnly(2026, 10, 1) };

    private static SoloDomainEvent Deleted(Guid taskId) =>
        new("task", taskId, ChangeKind.Deleted, [], Guid.NewGuid(), null, Guid.NewGuid());

    private async Task<List<string>> TablesAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_schema || '.' || table_name FROM information_schema.tables " +
            "WHERE table_schema NOT IN ('pg_catalog', 'information_schema')";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>Answers only the one question the handler asks: can the caller still find this task?</summary>
    private sealed class StubQuery(TaskDto? task) : ISoloQuery
    {
        public Task<TaskDto?> TaskAsync(Guid taskId, CancellationToken ct = default) => Task.FromResult(task);

        public Task<IReadOnlyList<ProjectSummaryDto>> ProjectsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BoardDto>> BoardsAsync(Guid projectId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EpicDto>> EpicsAsync(Guid boardId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TaskDto>> TasksAsync(Guid boardId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssigneeIdentity>> TaskAssigneeIdentitiesAsync(
            Guid taskId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> CanWriteBoardAsync(Guid boardId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ExtensionCaller> CallerAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
