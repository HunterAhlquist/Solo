using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Hubs;
namespace Solo.DesignDocs.Tests;

public sealed partial class VaultTests
{
    private sealed class Peer(HubConnection hub,Guid project,string topic) : IAsyncDisposable
    {
        private readonly Channel<TopicMessage> _messages=Channel.CreateUnbounded<TopicMessage>();
        public async Task Start(){hub.On<TopicMessage>(nameof(IBoardClient.TopicMessage),m=>_messages.Writer.TryWrite(m));await hub.StartAsync();await hub.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic),project,topic);}
        public Task<LiveSendResult> Send(string kind,object data)=>hub.InvokeAsync<LiveSendResult>(nameof(IBoardHubServer.SendTopicMessage),project,topic,kind,JsonSerializer.Serialize(data,TestClients.Json));
        public async Task<JsonElement> Next(string kind){using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(8));while(true){var m=await _messages.Reader.ReadAsync(cts.Token);if(m.Kind==kind)return JsonDocument.Parse(m.Data!).RootElement.Clone();}}
        public async ValueTask DisposeAsync()=>await hub.DisposeAsync();
    }
    private async Task<Peer> Connect(Guid id,string who="admin")
    {
        var hub=new HubConnectionBuilder().WithUrl(new Uri(_factory.Server.BaseAddress,BoardHubConstants.Path),o=>{
            o.HttpMessageHandlerFactory=_=>_factory.Server.CreateHandler();o.Transports=HttpTransportType.LongPolling;
            o.Headers[TestAuthHandler.SubHeader]=who;o.Headers[TestAuthHandler.EmailHeader]=who+"@example.com";o.Headers[TestAuthHandler.NameHeader]=who;
        }).Build();var p=new Peer(hub,_project.Id,DocTopics.Doc(id));await p.Start();return p;
    }
    private async Task<NoteDto> NewNote(VaultDto v)=>await _writer.PostJson<NoteDto>($"{Root}/{v.Id}/notes",new ItemChange("Note",null));
    private static readonly string[] Updates=["AQERAAQBCG1hcmtkb3duBUhlbGxvAA==","AQERBYQRBAYgd29ybGQA"];
    [SkippableFact]
    public async Task Opaque_updates_relay_ack_and_replay_after_reconnect()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);
        await using var a=await Connect(n.Id);await using var b=await Connect(n.Id,"writer");await using var reader=await Connect(n.Id,"reader");
        var id=Guid.NewGuid();Assert.Equal(LiveSendResult.Accepted,await a.Send("update",new {id,data=Updates[0]}));
        var ack=await a.Next("ack");Assert.Equal(id,ack.GetProperty("id").GetGuid());
        Assert.Equal(Updates[0],(await b.Next("update")).GetProperty("data").GetString());
        Assert.Equal(Updates[0],(await reader.Next("update")).GetProperty("data").GetString());
        await b.Send("update",new {id=Guid.NewGuid(),data=Updates[1]});await b.Next("ack");
        await using var reopened=await Connect(n.Id);
        await reopened.Send("sync",new {afterSeq="0"});
        Assert.Equal(Updates[0],(await reopened.Next("state")).GetProperty("data").GetString());
        Assert.Equal(Updates[1],(await reopened.Next("state")).GetProperty("data").GetString());
        Assert.True(long.Parse((await reopened.Next("synced")).GetProperty("seq").GetString()!)>0);
    }
    [SkippableFact]
    public async Task Read_and_closed_vault_updates_are_refused_without_storage()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);
        await using var reader=await Connect(n.Id,"reader");await reader.Send("update",new{id=Guid.NewGuid(),data=Updates[0]});
        Assert.Equal("read-only",(await reader.Next("refused")).GetProperty("reason").GetString());
        await _admin.PutJson<VaultDto>($"{Root}/{v.Id}",new VaultChange(v.Name,true,v.Version));
        await using var writer=await Connect(n.Id,"writer");await writer.Send("update",new{id=Guid.NewGuid(),data=Updates[0]});
        Assert.Equal("closed",(await writer.Next("refused")).GetProperty("reason").GetString());
        await writer.Send("sync",new {afterSeq="0"});Assert.Equal("0",(await writer.Next("synced")).GetProperty("seq").GetString());
    }
    [SkippableFact]
    public async Task Multipart_reassembly_and_compaction_keep_snapshot_then_tail()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);await using var p=await Connect(n.Id);
        var id=Guid.NewGuid();var data=Convert.ToBase64String(Enumerable.Range(0,40000).Select(i=>(byte)i).ToArray());
        await p.Send("update-part",new{id,index=1,count=2,data=data[32000..]});
        await p.Send("update-part",new{id,index=0,count=2,data=data[..32000]});var seq=(await p.Next("ack")).GetProperty("seq").GetString();
        await p.Send("update",new{id=Guid.NewGuid(),data=Updates[1]});await p.Next("ack");
        await p.Send("compact",new{uptoSeq=seq,data=Updates[0]});
        await p.Send("sync",new{afterSeq="0"});Assert.Equal(Updates[0],(await p.Next("state")).GetProperty("data").GetString());
        Assert.Equal(Updates[1],(await p.Next("state")).GetProperty("data").GetString());
        Assert.Equal(1,(await p.Next("synced")).GetProperty("updates").GetInt32());
    }
    [SkippableFact]
    public async Task Seed_lease_has_one_holder_and_accepted_retry_does_not_duplicate()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);
        await using(var c=new NpgsqlConnection(_connectionString)){await c.OpenAsync();await using var cmd=new NpgsqlCommand("UPDATE ext_design_docs.\"Notes\" SET \"SeedMarkdown\"='Hello' WHERE \"Id\"=@id",c);cmd.Parameters.AddWithValue("id",n.Id);await cmd.ExecuteNonQueryAsync();}
        await using var a=await Connect(n.Id);await using var b=await Connect(n.Id,"writer");
        await a.Send("sync",new{afterSeq="0"});var token=(await a.Next("seed")).GetProperty("seedToken").GetGuid();
        await b.Send("sync",new{afterSeq="0"});await b.Next("seeding");
        var update=new{id=Guid.NewGuid(),data=Updates[0],seedToken=token};
        await a.Send("update",update);var first=await a.Next("ack");await a.Send("update",update);
        Assert.Equal(first.GetProperty("seq").GetString(),(await a.Next("ack")).GetProperty("seq").GetString());
        await b.Send("sync",new{afterSeq="0"});Assert.Equal(Updates[0],(await b.Next("state")).GetProperty("data").GetString());
        Assert.Equal(1,(await b.Next("synced")).GetProperty("updates").GetInt32());
    }
    private static byte[][] Unpack(JsonElement payload)
    {
        using var stream=new MemoryStream(Convert.FromBase64String(payload.GetProperty("data").GetString()!));
        using var reader=new BinaryReader(stream);
        var count=reader.ReadInt32();var updates=new byte[count][];
        for(var i=0;i<count;i++)updates[i]=reader.ReadBytes(reader.ReadInt32());
        Assert.Equal(stream.Length,stream.Position);return updates;
    }
    [SkippableFact]
    public async Task Batched_replay_resumes_from_checkpoint_and_includes_compacted_snapshots()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);await using var p=await Connect(n.Id);
        await p.Send("update",new{id=Guid.NewGuid(),data=Updates[0]});var checkpoint=(await p.Next("ack")).GetProperty("seq").GetString();
        await p.Send("update",new{id=Guid.NewGuid(),data=Updates[1]});var head=(await p.Next("ack")).GetProperty("seq").GetString();
        var syncId=Guid.NewGuid();await p.Send("sync",new{afterSeq="0",batch=true,syncId});
        var batch=await p.Next("state-batch");Assert.Equal(syncId,batch.GetProperty("syncId").GetGuid());
        Assert.Equal(Updates,Unpack(batch).Select(Convert.ToBase64String));await p.Next("synced");
        await p.Send("sync",new{afterSeq=checkpoint,batch=true,syncId});
        Assert.Equal(Updates[1],Convert.ToBase64String(Assert.Single(Unpack(await p.Next("state-batch")))));
        Assert.Equal(head,(await p.Next("synced")).GetProperty("seq").GetString());
        await p.Send("compact",new{uptoSeq=checkpoint,data=Updates[0]});
        await p.Send("sync",new{afterSeq=checkpoint,batch=true,syncId});
        Assert.Equal(Updates,Unpack(await p.Next("state-batch")).Select(Convert.ToBase64String));
        var marker=await p.Next("synced");Assert.Equal(1,marker.GetProperty("updates").GetInt32());
        await p.Send("sync",new{afterSeq=marker.GetProperty("seq").GetString(),batch=true,syncId});
        Assert.Equal(marker.GetProperty("seq").GetString(),(await p.Next("synced")).GetProperty("seq").GetString());
    }
    [SkippableFact]
    public async Task Lost_ack_retry_returns_the_same_sequence_without_duplicate_history()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);await using var p=await Connect(n.Id);
        var id=Guid.NewGuid();await p.Send("update",new{id,data=Updates[0]});var first=await p.Next("ack");
        await p.Send("update",new{id,data=Updates[0]});Assert.Equal(first.GetProperty("seq").GetString(),(await p.Next("ack")).GetProperty("seq").GetString());
        await p.Send("update",new{id,data=Updates[1]});Assert.Equal("invalid-id",(await p.Next("refused")).GetProperty("reason").GetString());
        await p.Send("sync",new{afterSeq="0",batch=true});
        Assert.Single(Unpack(await p.Next("state-batch")));Assert.Equal(1,(await p.Next("synced")).GetProperty("updates").GetInt32());
    }

}
