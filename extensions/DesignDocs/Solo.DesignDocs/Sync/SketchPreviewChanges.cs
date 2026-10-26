using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;

namespace Solo.DesignDocs.Sync;

internal static class SketchPreviewChanges
{
    public static Task PublishAsync(ISoloLiveUpdates live,Guid projectId,Sketch sketch,CancellationToken ct,string? previousName=null)
        =>live.PublishAsync(projectId,DocTopics.Vault(sketch.VaultId),"sketch",DocProtocol.Serialize(new SketchPreviewChange(sketch.NoteId,sketch.Name,AttachmentPaths.ForNote(sketch.NoteId,sketch.Name+".sketch"),previousName)),ct);
}
