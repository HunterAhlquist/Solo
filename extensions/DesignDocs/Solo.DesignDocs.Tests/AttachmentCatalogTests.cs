using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
namespace Solo.DesignDocs.Tests;

public sealed class AttachmentCatalogTests
{
    private static readonly Guid Vault=Guid.NewGuid();
    private static NoteDto Note(Guid id,string name,Guid? folder=null)=>new(id,Vault,folder,name,"","0",[],null,null,1);
    private static ImageDto Image(string path)=>new(Guid.NewGuid(),path,"image/png",4,3,10,1);
    private static SketchDto Sketch(Guid note,string name)=>new(Guid.NewGuid(),note,name,"A4","portrait","dots",null,null,1);

    [Fact]
    public void Embed_targets_skip_code_and_keep_both_embed_forms()
    {
        var markdown="![[a.png|wide]] ![alt](b%20c.webp \"t\")\n`![[inline.png]]`\n```\n![[fenced.png]]\n```\n![[Map.sketch]]";
        Assert.Equal(["a.png|wide","b%20c.webp","Map.sketch"],AttachmentPaths.EmbedTargets(markdown));
        Assert.Equal("b c.webp",AttachmentPaths.Target("b%20c.webp"));Assert.Equal("a.png",AttachmentPaths.Target("a.png|wide"));
    }
    [Fact]
    public void Owners_come_from_attachment_paths_only()
    {
        var note=Guid.NewGuid();
        Assert.Equal(note,AttachmentPaths.Owner(AttachmentPaths.ForNote(note,"photo.png")));
        Assert.Null(AttachmentPaths.Owner("assets/photo.png"));Assert.Null(AttachmentPaths.Owner("attachments/not-a-key/photo.png"));
    }
    [Fact]
    public void Each_attachment_knows_where_it_is_embedded_and_what_no_note_embeds()
    {
        Guid here=Guid.NewGuid(),other=Guid.NewGuid();
        var mine=Image(AttachmentPaths.ForNote(here,"photo.png"));var lost=Image(AttachmentPaths.ForNote(here,"old.png"));var legacy=Image("art/logo.png");
        var map=Sketch(here,"Map");var flow=Sketch(other,"Flow");var unused=Sketch(other,"Unused");
        var list=new AttachmentListDto([mine,lost,legacy],[map,flow,unused],new Dictionary<Guid,string[]>{
            // The saved copy of this note is stale; the editor's live targets win.
            [here]=["old.png"],
            [other]=["Flow.sketch","logo.png",mine.Path,"Map.sketch"]});
        var tree=new TreeDto(new(Vault,"Design",false,null,1),[],[Note(here,"Here"),Note(other,"Other")]);
        var items=AttachmentCatalog.Build(list,tree,here,[mine.Path+"|300","Map.sketch",flow.Path]).ToDictionary(i=>i.Id);
        Assert.Equal(new HashSet<Guid>{here,other},items[mine.Id].EmbeddedIn.ToHashSet());Assert.Equal([mine.Path+"|300"],items[mine.Id].Targets);
        Assert.Empty(items[lost.Id].EmbeddedIn);Assert.Equal(here,items[lost.Id].OwnerId);
        // Only the owning note resolves a sketch by its bare name.
        Assert.Equal([here],items[map.Id].EmbeddedIn);
        Assert.Equal(new HashSet<Guid>{here,other},items[flow.Id].EmbeddedIn.ToHashSet());Assert.Equal("Other",items[flow.Id].OwnerName);Assert.True(items[flow.Id].InThisNote(here));
        Assert.Empty(items[unused.Id].EmbeddedIn);Assert.False(items[unused.Id].InThisNote(here));
        Assert.Null(items[legacy.Id].OwnerId);Assert.Equal([other],items[legacy.Id].EmbeddedIn);Assert.Equal("4 × 3",items[legacy.Id].Detail);
    }
    [Fact]
    public void An_ambiguous_file_name_embeds_nothing()
    {
        Guid a=Guid.NewGuid(),b=Guid.NewGuid();var first=Image(AttachmentPaths.ForNote(a,"photo.png"));var second=Image(AttachmentPaths.ForNote(b,"photo.png"));
        var tree=new TreeDto(new(Vault,"Design",false,null,1),[],[Note(a,"A"),Note(b,"B")]);
        var items=AttachmentCatalog.Build(new([first,second],[],new Dictionary<Guid,string[]>{[a]=[],[b]=[]}),tree,a,["photo.png"]);
        Assert.All(items,i=>Assert.Empty(i.EmbeddedIn));
    }
}
