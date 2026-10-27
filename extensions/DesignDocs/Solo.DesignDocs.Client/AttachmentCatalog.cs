using Solo.DesignDocs.Contracts;
namespace Solo.DesignDocs.Client;

// One row of the attachments dialog. Targets are the raw embed targets in the open note that
// resolve to it, so deleting it can remove exactly those embeds.
public sealed record AttachmentItem(Guid Id,bool Sketch,string Path,string Name,Guid? OwnerId,string? OwnerName,
    IReadOnlyList<Guid> EmbeddedIn,IReadOnlyList<string> Targets,string? Detail)
{
    public bool InThisNote(Guid note)=>OwnerId==note||EmbeddedIn.Contains(note);
}

public static class AttachmentCatalog
{
    // A sketch embed resolves by full path from any note, or by name or pending path from the
    // note that owns it; an image embed resolves as the previews do (exact, pending, relative,
    // then an unambiguous file name). Keeping this beside the editor's own lookups is what makes
    // "Not embedded" mean the same thing as a preview that can't load.
    public static bool MatchesSketch(SketchDto sketch,string name,Guid note)=>sketch.Path==name+".sketch"||
        sketch.NoteId==note&&(sketch.Name==name||Pending(name,sketch.Id,note));
    public static bool Pending(string path,Guid id,Guid note)=>path.StartsWith(AttachmentPaths.ForNote(note,id+"/"),StringComparison.Ordinal)||path.StartsWith($"offline/{id}/",StringComparison.Ordinal);

    public static List<AttachmentItem> Build(AttachmentListDto list,TreeDto tree,Guid current,IReadOnlyList<string>? live)
    {
        var folders=tree.Folders.ToDictionary(f=>f.Id);
        string FolderPath(Guid? id){var parts=new List<string>();for(var p=id;p is {} f&&folders.TryGetValue(f,out var folder);p=folder.ParentId)parts.Insert(0,folder.Name);return parts.Count==0?"":string.Join('/',parts)+"/";}
        var notes=tree.Notes.ToDictionary(n=>n.Id);
        var embeds=new Dictionary<Guid,string[]>(list.Embeds);if(live is not null)embeds[current]=[..live];
        var paths=list.Images.Select(i=>i.Path).ToArray();
        var embeddedIn=new Dictionary<Guid,HashSet<Guid>>();var targets=new Dictionary<Guid,List<string>>();
        foreach(var (noteId,raws) in embeds){
            var notePath=notes.TryGetValue(noteId,out var n)?FolderPath(n.FolderId)+n.Name+".md":"";
            foreach(var raw in raws){
                var target=AttachmentPaths.Target(raw);Guid? hit=null;
                if(target.EndsWith(".sketch",StringComparison.OrdinalIgnoreCase)){
                    var name=target[..^7];hit=list.Sketches.FirstOrDefault(s=>s.Path==target)?.Id??list.Sketches.FirstOrDefault(s=>MatchesSketch(s,name,noteId))?.Id;
                }else{
                    var image=list.Images.FirstOrDefault(i=>i.Path==target)??list.Images.FirstOrDefault(i=>Pending(target,i.Id,noteId));
                    if(image is null&&VaultPaths.ResolveImage(paths,target,notePath) is {} resolved)image=list.Images.First(i=>i.Path==resolved);
                    hit=image?.Id;
                }
                if(hit is not {} id)continue;
                (embeddedIn.TryGetValue(id,out var set)?set:embeddedIn[id]=[]).Add(noteId);
                if(noteId==current)(targets.TryGetValue(id,out var mine)?mine:targets[id]=[]).Add(raw);
            }
        }
        AttachmentItem Item(Guid id,bool sketch,string path,Guid? owner,string? detail)=>new(id,sketch,path,AttachmentPaths.FileName(path),owner,
            owner is {} o&&notes.TryGetValue(o,out var note)?note.Name:null,
            embeddedIn.TryGetValue(id,out var where)?[..where]:[],targets.TryGetValue(id,out var raws)?raws.Distinct().ToList():[],detail);
        return list.Images.Select(i=>Item(i.Id,false,i.Path,AttachmentPaths.Owner(i.Path) is {} o&&notes.ContainsKey(o)?o:null,i.Width>0?$"{i.Width} × {i.Height}":null))
            .Concat(list.Sketches.Select(s=>Item(s.Id,true,s.Path,s.NoteId,null)))
            .OrderBy(i=>i.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
