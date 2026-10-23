using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.DesignDocs.Tests;

/// <summary>
/// The relay over core's real hub: what each person hears, in what order, and
/// what the server keeps. The bytes are opaque to the server, so these send
/// arbitrary ones; js/test/provider.test.js covers what Yjs does with them.
/// </summary>
[Collection("postgres")]
public sealed class LiveSyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Base = "/api/ext/design-docs";

    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private ProjectDto _project = null!;
    private Guid _vault;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("designdocslive"),
            [SoloApiFactory.PackageDirectory("design-docs")]);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
        _project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Live docs", alias = "DDLIVE" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members",
            new { email = "writer@example.com", role = "ReadWrite" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members",
            new { email = "reader@example.com", role = "Read" });
        _vault = (await _admin.PostJson<VaultDto>($"{Base}/projects/{_project.Id}/vaults", new { name = "Live" })).Id;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Writers_hear_each_other_in_seq_order_and_a_late_reader_catches_up()
    {
        postgres.SkipUnlessDockerAvailable();
        var note = await NoteAsync("Shared");
        await using var a = await PeerAsync("admin", note);
        await using var b = await PeerAsync("writer", note);
        await a.SyncAsync(0);
        await b.SyncAsync(0);

        for (var i = 0; i < 5; i++)
        {
            await a.UpdateAsync($"a{i}", [1, (byte)i]);
            await b.UpdateAsync($"b{i}", [2, (byte)i]);
        }

        var heardByA = await a.RelayedAsync(5);
        var heardByB = await b.RelayedAsync(5);
        Assert.Equal(heardByA.Select(u => u.Seq).Order(), heardByA.Select(u => u.Seq));
        Assert.Equal(heardByB.Select(u => u.Seq).Order(), heardByB.Select(u => u.Seq));
        Assert.All(heardByA, u => Assert.Equal(2, Convert.FromBase64String(u.Data)[0]));

        await using var reader = await PeerAsync("reader", note);
        var (entries, synced) = await reader.SyncAsync(0);
        Assert.Equal(10, entries.Count);
        Assert.Equal(entries[^1].Seq, synced.Seq);

        // Resyncing from where you were sends only what came after.
        var (later, _) = await reader.SyncAsync(entries[4].Seq);
        Assert.Equal(entries.Skip(5).Select(e => e.Seq), later.Select(e => e.Seq));
    }

    [SkippableFact]
    public async Task A_reader_is_refused_and_heard_by_nobody()
    {
        postgres.SkipUnlessDockerAvailable();
        var note = await NoteAsync("Readonly");
        await using var writer = await PeerAsync("writer", note);
        await using var reader = await PeerAsync("reader", note);
        await writer.SyncAsync(0);
        await reader.SyncAsync(0);

        Assert.Equal("read-only", await reader.RefusedAsync("r1", [9]));
        await reader.SendAsync("awareness", Convert.ToBase64String([1, 2, 3]));
        await Task.Delay(300);

        Assert.DoesNotContain(writer.Heard, m => m.Kind is "update" or "awareness");
        var (entries, _) = await writer.SyncAsync(0);
        Assert.Empty(entries);
    }

    [SkippableFact]
    public async Task A_closed_vault_refuses_the_next_update()
    {
        postgres.SkipUnlessDockerAvailable();
        var vault = (await _admin.PostJson<VaultDto>($"{Base}/projects/{_project.Id}/vaults", new { name = "Closing" })).Id;
        var note = await _admin.PostJson<NoteSummaryDto>($"{Base}/projects/{_project.Id}/vaults/{vault}/notes", new { name = "Last words" });
        await using var writer = await PeerAsync("writer", note.Id);
        await writer.SyncAsync(0);
        await writer.UpdateAsync("w1", [1]);

        (await _admin.PostAsync($"{Base}/projects/{_project.Id}/vaults/{vault}/close", null)).EnsureSuccessStatusCode();

        Assert.Equal("closed", await writer.RefusedAsync("w2", [2]));
    }

    /// <summary>An imported note is converted once, by whichever writer gets the lease; the other waits and loads it.</summary>
    [SkippableFact]
    public async Task Two_writers_opening_an_imported_note_seed_it_once()
    {
        postgres.SkipUnlessDockerAvailable();
        var result = await _admin.PostJson<ImportResultDto>($"{Base}/projects/{_project.Id}/vaults/{_vault}/import/notes",
            new { notes = new[] { new { path = "Imported.md", markdown = "# From Obsidian" } } });
        Assert.Equal(1, result.NotesCreated);
        var tree = await _admin.GetJson<VaultTreeDto>($"{Base}/projects/{_project.Id}/vaults/{_vault}/tree");
        var note = tree.Notes.Single(n => n.Name == "Imported").Id;

        await using var a = await PeerAsync("admin", note);
        await using var b = await PeerAsync("writer", note);
        var answers = await Task.WhenAll(a.FirstAnswerToSyncAsync(), b.FirstAnswerToSyncAsync());

        Assert.Single(answers, m => m.Kind == "seed");
        Assert.Single(answers, m => m.Kind == "seeding");
        var (seeder, waiter) = answers[0].Kind == "seed" ? (a, b) : (b, a);
        var seed = JsonSerializer.Deserialize<JsonElement>(answers.Single(m => m.Kind == "seed").Data!);
        Assert.Equal("# From Obsidian", seed.GetProperty("markdown").GetString());

        // Only the lease holder's seed lands; the other's would double the text.
        Assert.Equal("unseeded", await waiter.RefusedAsync("early", [7]));
        await seeder.UpdateAsync("seed", [42], seed.GetProperty("lease").GetString());
        Assert.Equal("seed-lost", await waiter.RefusedAsync("late", [43], lease: "not-a-lease"));

        var (entries, _) = await waiter.SyncAsync(0);
        Assert.Equal([42], Convert.FromBase64String(Assert.Single(entries).Data));
        var log = await _admin.GetJson<DocumentLogDto>($"{Base}/projects/{_project.Id}/vaults/{_vault}/documents/{note}/log");
        Assert.Null(log.SeedMarkdown);
    }

    [SkippableFact]
    public async Task A_reader_opening_an_imported_note_sees_the_text_unseeded()
    {
        postgres.SkipUnlessDockerAvailable();
        await _admin.PostJson<ImportResultDto>($"{Base}/projects/{_project.Id}/vaults/{_vault}/import/notes",
            new { notes = new[] { new { path = "Untouched.md", markdown = "As imported" } } });
        var tree = await _admin.GetJson<VaultTreeDto>($"{Base}/projects/{_project.Id}/vaults/{_vault}/tree");
        await using var reader = await PeerAsync("reader", tree.Notes.Single(n => n.Name == "Untouched").Id);

        var answer = await reader.FirstAnswerToSyncAsync();

        Assert.Equal("unseeded", answer.Kind);
        Assert.Equal("As imported", JsonSerializer.Deserialize<JsonElement>(answer.Data!).GetProperty("markdown").GetString());
    }

    /// <summary>An update too long for one hub message arrives in parts and is stored whole.</summary>
    [SkippableFact]
    public async Task A_long_update_arrives_in_parts()
    {
        postgres.SkipUnlessDockerAvailable();
        var note = await NoteAsync("Long");
        await using var a = await PeerAsync("admin", note);
        await using var b = await PeerAsync("writer", note);
        await a.SyncAsync(0);
        await b.SyncAsync(0);
        var bytes = new byte[120_000];
        Random.Shared.NextBytes(bytes);

        var json = JsonSerializer.Serialize(new { id = "big", data = Convert.ToBase64String(bytes) });
        const int chunk = 46_000;
        var parts = (json.Length + chunk - 1) / chunk;
        for (var i = 0; i < parts; i++)
        {
            await a.SendAsync("part", JsonSerializer.Serialize(new
            {
                k = "update", id = "p1", i, n = parts, d = json.Substring(i * chunk, Math.Min(chunk, json.Length - i * chunk)),
            }));
        }

        await a.AckAsync("big");
        var (entries, _) = await b.SyncAsync(0);
        Assert.Equal(bytes, Convert.FromBase64String(Assert.Single(entries).Data));
    }

    // -- Helpers ------------------------------------------------------------------------

    private async Task<Guid> NoteAsync(string name) =>
        (await _admin.PostJson<NoteSummaryDto>($"{Base}/projects/{_project.Id}/vaults/{_vault}/notes", new { name })).Id;

    private async Task<Peer> PeerAsync(string sub, Guid documentId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubConstants.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.SubHeader] = sub;
                options.Headers[TestAuthHandler.EmailHeader] = $"{sub}@example.com";
                options.Headers[TestAuthHandler.NameHeader] = sub;
            })
            .Build();
        var peer = new Peer(connection, _project.Id, DocsTopics.Document(documentId));
        await connection.StartAsync();
        await connection.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), _project.Id, peer.Topic);
        return peer;
    }

    private sealed record Relayed(long Seq, string Data);

    private sealed record Entry(long Seq, string Data);

    private sealed record Synced(long Seq, int Pending);

    private sealed class Peer : IAsyncDisposable
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
        private readonly HubConnection _connection;
        private readonly Guid _projectId;
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly ConcurrentDictionary<string, string?[]> _parts = new();

        public Peer(HubConnection connection, Guid projectId, string topic)
        {
            (_connection, _projectId, Topic) = (connection, projectId, topic);
            connection.On<TopicMessage>(nameof(IBoardClient.TopicMessage), message =>
            {
                if (message.Topic != Topic)
                {
                    return;
                }

                // A long answer comes in parts, as the browser module reassembles it.
                if (message.Kind == "part")
                {
                    var part = JsonSerializer.Deserialize<JsonElement>(message.Data!);
                    var id = part.GetProperty("id").GetString()!;
                    var slices = _parts.GetOrAdd(id, _ => new string?[part.GetProperty("n").GetInt32()]);
                    slices[part.GetProperty("i").GetInt32()] = part.GetProperty("d").GetString();
                    if (slices.Any(s => s is null))
                    {
                        return;
                    }

                    _parts.TryRemove(id, out _);
                    message = message with { Kind = part.GetProperty("k").GetString()!, Data = string.Concat(slices) };
                }

                Heard.Enqueue(message);
                _arrived.Release();
            });
        }

        public string Topic { get; }

        public ConcurrentQueue<TopicMessage> Heard { get; } = new();

        public async Task SendAsync(string kind, string? data)
        {
            var result = await _connection.InvokeAsync<LiveSendResult>(
                nameof(IBoardHubServer.SendTopicMessage), _projectId, Topic, kind, data);
            Assert.Equal(LiveSendResult.Accepted, result);
        }

        public async Task<(List<Entry> Entries, Synced Synced)> SyncAsync(long afterSeq)
        {
            Heard.Clear();
            await SendAsync("sync", JsonSerializer.Serialize(new { afterSeq }));
            var synced = await NextAsync("synced");
            var entries = Heard.Where(m => m.Kind == "state")
                .SelectMany(m => JsonSerializer.Deserialize<JsonElement>(m.Data!).GetProperty("entries").EnumerateArray())
                .Select(e => new Entry(e[0].GetInt64(), e[1].GetString()!))
                .ToList();
            var body = JsonSerializer.Deserialize<JsonElement>(synced.Data!);
            return (entries, new Synced(body.GetProperty("seq").GetInt64(), body.GetProperty("pending").GetInt32()));
        }

        public async Task<TopicMessage> FirstAnswerToSyncAsync()
        {
            Heard.Clear();
            await SendAsync("sync", JsonSerializer.Serialize(new { afterSeq = 0 }));
            return await NextAsync("seed", "seeding", "unseeded", "state", "synced");
        }

        public async Task UpdateAsync(string id, byte[] bytes, string? lease = null)
        {
            await SendAsync("update", JsonSerializer.Serialize(lease is null
                ? new { id, data = Convert.ToBase64String(bytes), lease = (string?)null }
                : new { id, data = Convert.ToBase64String(bytes), lease = (string?)lease }));
            await AckAsync(id);
        }

        public async Task AckAsync(string id)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (Heard.Any(m => m.Kind == "ack" && JsonSerializer.Deserialize<JsonElement>(m.Data!).GetProperty("id").GetString() == id))
                {
                    return;
                }

                await _arrived.WaitAsync(TimeSpan.FromMilliseconds(200));
            }

            Assert.Fail($"No ack for {id}.");
        }

        public async Task<string> RefusedAsync(string id, byte[] bytes, string? lease = null)
        {
            await SendAsync("update", JsonSerializer.Serialize(new { id, data = Convert.ToBase64String(bytes), lease }));
            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                var refused = Heard.Where(m => m.Kind == "refused")
                    .Select(m => JsonSerializer.Deserialize<JsonElement>(m.Data!))
                    .FirstOrDefault(m => m.GetProperty("id").GetString() == id);
                if (refused.ValueKind == JsonValueKind.Object)
                {
                    return refused.GetProperty("reason").GetString()!;
                }

                Assert.DoesNotContain(Heard, m => m.Kind == "ack" &&
                    JsonSerializer.Deserialize<JsonElement>(m.Data!).GetProperty("id").GetString() == id);
                await _arrived.WaitAsync(TimeSpan.FromMilliseconds(200));
            }

            Assert.Fail($"{id} was neither refused nor acked.");
            return "";
        }

        public async Task<List<Relayed>> RelayedAsync(int count)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                var relayed = Heard.Where(m => m.Kind == "update")
                    .Select(m => JsonSerializer.Deserialize<JsonElement>(m.Data!))
                    .Select(m => new Relayed(m.GetProperty("seq").GetInt64(), m.GetProperty("data").GetString()!))
                    .ToList();
                if (relayed.Count >= count)
                {
                    return relayed;
                }

                await _arrived.WaitAsync(TimeSpan.FromMilliseconds(200));
            }

            Assert.Fail($"Fewer than {count} updates were relayed.");
            return [];
        }

        private async Task<TopicMessage> NextAsync(params string[] kinds)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (Heard.FirstOrDefault(m => kinds.Contains(m.Kind)) is { } found)
                {
                    return found;
                }

                await _arrived.WaitAsync(TimeSpan.FromMilliseconds(200));
            }

            Assert.Fail($"Nothing of kind {string.Join("/", kinds)} arrived.");
            return null!;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
