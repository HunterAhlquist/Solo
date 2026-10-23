using System.Text.Json;
using System.Text.Json.Nodes;
using Solo.DesignDocs.Live;

namespace Solo.DesignDocs.Data.Tests;

/// <summary>
/// The wire format, pinned by the examples the JS tests read too: each one
/// reads into the C# record for its kind and writes back as the same JSON.
/// </summary>
public sealed class SyncProtocolTests
{
    private static readonly JsonObject Examples =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "protocol-examples.json")))!.AsObject();

    public static IEnumerable<object[]> Cases() =>
        Examples["examples"]!.AsArray().Select((e, i) => new object[] { i, e!["kind"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_example_round_trips_through_its_record(int index, string kind)
    {
        var data = Examples["examples"]![index]!["data"]!.ToJsonString();
        var type = kind switch
        {
            SyncProtocol.Sync => typeof(SyncMessage),
            SyncProtocol.Update when data.Contains("\"seq\"") => typeof(RelayedUpdate),
            SyncProtocol.Update => typeof(UpdateMessage),
            SyncProtocol.Compact => typeof(CompactMessage),
            SyncProtocol.State => typeof(StateMessage),
            SyncProtocol.Synced => typeof(SyncedMessage),
            SyncProtocol.Ack => typeof(AckMessage),
            SyncProtocol.Refused => typeof(RefusedMessage),
            SyncProtocol.Seed => typeof(SeedMessage),
            SyncProtocol.Unseeded => typeof(UnseededMessage),
            SyncProtocol.Part => typeof(PartMessage),
            _ => throw new InvalidOperationException($"No record for kind {kind}."),
        };

        var record = JsonSerializer.Deserialize(data, type, SyncProtocol.Json);
        var written = JsonSerializer.Serialize(record, type, SyncProtocol.Json);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data), JsonNode.Parse(written)), $"{kind}: wrote {written}");
    }

    [Fact]
    public void The_refusal_reasons_and_chunk_length_match()
    {
        var reasons = typeof(SyncProtocol.Reasons).GetFields()
            .Select(f => (string)f.GetRawConstantValue()!).Order().ToList();
        Assert.Equal(Examples["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).Order(), reasons);
        Assert.Equal(Examples["chunkLength"]!.GetValue<int>(), SyncProtocol.ChunkLength);
    }
}
