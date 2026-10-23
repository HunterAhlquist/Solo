using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solo.DesignDocs.Live;

/// <summary>
/// The relay protocol on a document's topic: message kinds and their data. The
/// browser's copy is <c>Solo.DesignDocs.Client/js/src/sync/protocol.js</c>; both
/// are pinned by <c>protocol-examples.json</c>, which the C# and JS tests read.
/// </summary>
/// <remarks>
/// <para>Yjs payloads travel as base64 strings, since core's hub speaks JSON.
/// Everything else is a small JSON object, so the shapes stay readable in a
/// network trace.</para>
///
/// <para>Any message whose data is too long for one hub message travels as
/// <see cref="Part"/> messages in either direction, and is handled as its
/// original kind once whole. That is one rule for every kind, where the plan
/// had an <c>update-part</c> for updates alone: a seed, a state reply or a
/// compaction can be as large as an update.</para>
/// </remarks>
public static class SyncProtocol
{
    // Client to server.
    public const string Sync = "sync";
    public const string Update = "update";
    public const string Awareness = "awareness";
    public const string Compact = "compact";

    // Server to client. Update and Awareness are also relayed under their own names.
    public const string State = "state";
    public const string Synced = "synced";
    public const string Ack = "ack";
    public const string Refused = "refused";
    public const string Seed = "seed";
    public const string Seeding = "seeding";
    public const string Unseeded = "unseeded";

    /// <summary>Either direction: one slice of a message too long to send whole.</summary>
    public const string Part = "part";

    /// <summary>
    /// Data per hub message, below the hub's own cap
    /// (<c>BoardHubConstants.ClientMessageMaxLength</c>, 48,000) to leave room
    /// for a part's envelope.
    /// </summary>
    public const int ChunkLength = 46_000;

    /// <summary>The most a reassembled message may hold. A whole large note's snapshot fits with room to spare.</summary>
    public const int MaxMessageLength = 16 * 1024 * 1024;

    public static class Reasons
    {
        /// <summary>The sender can only read this project.</summary>
        public const string ReadOnly = "read-only";

        /// <summary>The vault is closed.</summary>
        public const string Closed = "closed";

        /// <summary>The note is waiting to be seeded, and this update isn't the seed.</summary>
        public const string Unseeded = "unseeded";

        /// <summary>A seed that lost: its lease ran out, or someone else's seed landed first. Resync.</summary>
        public const string SeedLost = "seed-lost";

        /// <summary>The message didn't parse.</summary>
        public const string Malformed = "malformed";
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Read<T>(string? data) where T : class
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(data, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="data"/> as one message, or as parts of at most
    /// <see cref="ChunkLength"/> characters each when it's longer.
    /// </summary>
    public static IEnumerable<(string Kind, string? Data)> Frame(string kind, string? data)
    {
        if (data is null || data.Length <= ChunkLength)
        {
            yield return (kind, data);
            yield break;
        }

        var id = Guid.NewGuid().ToString("N");
        var count = (data.Length + ChunkLength - 1) / ChunkLength;
        for (var index = 0; index < count; index++)
        {
            var start = index * ChunkLength;
            var slice = data.Substring(start, Math.Min(ChunkLength, data.Length - start));
            yield return (Part, Write(new PartMessage(kind, id, index, count, slice)));
        }
    }
}

/// <summary><c>sync</c>: the newest entry the sender already has, or 0 for none.</summary>
public sealed record SyncMessage(long AfterSeq);

/// <summary>
/// <c>update</c> from a client: a Yjs update under the client's own id for it,
/// which the ack echoes. <see cref="Lease"/> marks it as the seed of an imported note.
/// </summary>
public sealed record UpdateMessage(string Id, string Data, string? Lease = null);

/// <summary><c>update</c> relayed to the others, under the seq it was stored at.</summary>
public sealed record RelayedUpdate(long Seq, string Data);

/// <summary><c>compact</c>: a snapshot that stands for every entry up to and including <see cref="UptoSeq"/>.</summary>
public sealed record CompactMessage(long UptoSeq, string Data);

/// <summary><c>state</c>: log entries in order, as <c>[seq, base64]</c> pairs.</summary>
public sealed record StateMessage(IReadOnlyList<StateEntry> Entries);

[JsonConverter(typeof(StateEntryConverter))]
public sealed record StateEntry(long Seq, string Data);

/// <summary>
/// <c>synced</c>: the sync is complete, through <see cref="Seq"/>.
/// <see cref="Pending"/> counts the entries since the last snapshot, so a
/// writer can tell when the log is due for compacting.
/// </summary>
public sealed record SyncedMessage(long Seq, int Pending);

public sealed record AckMessage(string Id, long Seq);

public sealed record RefusedMessage(string Id, string Reason);

/// <summary><c>seed</c>: the imported text to convert, under a lease the seed update must carry.</summary>
public sealed record SeedMessage(string Markdown, string Lease);

/// <summary><c>unseeded</c>: the imported text, for a reader to show until a writer seeds it.</summary>
public sealed record UnseededMessage(string Markdown);

/// <summary><c>part</c>: slice <see cref="I"/> of <see cref="N"/> of a <see cref="K"/> message's data.</summary>
public sealed record PartMessage(string K, string Id, int I, int N, string D);

/// <summary>Writes a state entry as a two-element array, which keeps a long state reply short.</summary>
internal sealed class StateEntryConverter : JsonConverter<StateEntry>
{
    public override StateEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
        {
            throw new JsonException("A state entry is [seq, data].");
        }

        var seq = reader.GetInt64();
        reader.Read();
        var data = reader.GetString() ?? throw new JsonException("A state entry has data.");
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A state entry is [seq, data].");
        }

        return new StateEntry(seq, data);
    }

    public override void Write(Utf8JsonWriter writer, StateEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Seq);
        writer.WriteStringValue(value.Data);
        writer.WriteEndArray();
    }
}
