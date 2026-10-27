using System.Text.RegularExpressions;
namespace Solo.DesignDocs.Contracts;

public static class AttachmentPaths
{
    public static string ForNote(Guid noteId, string name) => $"attachments/{NoteAddress.Encode(noteId)}/{name}";
    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
    // The note an attachment was created in, read from its path. Imported files keep their
    // original paths and have none.
    public static Guid? Owner(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 2 && parts[0] == "attachments" && NoteAddress.TryDecode(parts[1], out var id) ? id : null;
    }

    private static readonly Regex Code = new(@"^(```|~~~)[^\n]*\n[\s\S]*?(^\1[^\n]*$|\z)|`[^`\n]*`", RegexOptions.Multiline);
    private static readonly Regex Embed = new(@"!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+""[^""]*"")?\)");
    // The raw targets of a note's image and sketch embeds, as the editor reads them, outside
    // code. js/media/vault-paths.js imageTargets is the same rule for the live editor and
    // downloaded vaults.
    public static string[] EmbedTargets(string? markdown) => string.IsNullOrEmpty(markdown) ? [] :
        Embed.Matches(Code.Replace(markdown, m => new string(' ', m.Length)))
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToArray();
    // A raw target as the editor resolves it: the size or alias after | dropped, URL-decoded.
    public static string Target(string raw)
    {
        var path = raw.Split('|')[0];
        try { return Uri.UnescapeDataString(path); } catch (UriFormatException) { return path; }
    }
}
