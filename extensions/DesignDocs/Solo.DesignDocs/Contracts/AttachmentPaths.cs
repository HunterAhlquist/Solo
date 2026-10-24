namespace Solo.DesignDocs.Contracts;

public static class AttachmentPaths
{
    public static string ForNote(Guid noteId, string name) => $"attachments/{NoteAddress.Encode(noteId)}/{name}";
    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
