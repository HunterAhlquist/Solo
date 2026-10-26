using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Solo.DesignDocs.Contracts;

namespace Solo.DesignDocs.Tests;

public sealed partial class VaultTests
{
    [SkippableFact]
    public async Task Offline_reads_do_not_wait_for_a_vault_write_lock()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);
        await using var connection=new NpgsqlConnection(_connectionString);await connection.OpenAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using var command=new NpgsqlCommand("SELECT 1 FROM ext_design_docs.\"Vaults\" WHERE \"Id\"=@id FOR UPDATE",connection,transaction);
        command.Parameters.AddWithValue("id",vault.Id);await command.ExecuteScalarAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var root=$"{Root}/{vault.Id}/offline";
        (await _reader.GetAsync(root,timeout.Token)).EnsureSuccessStatusCode();
        (await _reader.GetAsync(root+"/documents/"+note.Id,timeout.Token)).EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task Deleting_an_empty_folder_does_not_query_unrelated_documents()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var root=$"{Root}/{vault.Id}";
        var folder=await _writer.PostJson<FolderDto>(root+"/folders",new ItemChange("Empty",null));
        for(var i=0;i<8;i++)await _writer.PostJson<NoteDto>(root+"/notes",new ItemChange("Unrelated "+i,null));
        using var capture=new CommandCapture(new NpgsqlConnectionStringBuilder(_connectionString).Database!);
        await Command(vault,new(Guid.NewGuid(),"folder","delete",folder.Id,folder.Version));
        Assert.True(capture.Commands.Count<15,$"An empty folder deletion executed {capture.Commands.Count} commands.");
        Assert.Equal(8,(await _reader.GetJson<TreeDto>(root)).Notes.Count);
    }

    private sealed class CommandCapture:IObserver<DiagnosticListener>,IObserver<KeyValuePair<string,object?>>,IDisposable
    {
        private readonly string _database;private readonly List<IDisposable> _subscriptions=[];
        public ConcurrentQueue<string> Commands{get;}=new();
        public CommandCapture(string database){_database=database;_subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));}
        public void OnNext(DiagnosticListener value){if(value.Name=="Microsoft.EntityFrameworkCore")_subscriptions.Add(value.Subscribe(this));}
        public void OnNext(KeyValuePair<string,object?> value){if(value.Value is CommandExecutedEventData data&&data.Command.Connection?.Database==_database)Commands.Enqueue(data.Command.CommandText);}
        public void OnCompleted(){}public void OnError(Exception error){}
        public void Dispose(){foreach(var subscription in _subscriptions)subscription.Dispose();}
    }
}
