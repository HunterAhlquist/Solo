namespace Solo.DesignDocs.Client;

public static class VaultPaths
{
    // Paths arrive URL-decoded from the editor. Preserve exact-path precedence,
    // then use the note's directory before attempting an unambiguous basename.
    public static string? ResolveImage(IReadOnlyList<string> paths, string target, string notePath)
    {
        if (target.StartsWith('/') || Uri.TryCreate(target, UriKind.Absolute, out _)) return null;
        var exact = Normalize(target);
        if (exact is not null && paths.Contains(exact)) return exact;
        var slash = notePath.LastIndexOf('/');
        var directory = slash >= 0 ? notePath[..(slash + 1)] : "";
        var relative = Normalize(directory + target);
        if (relative is not null && paths.Contains(relative)) return relative;
        var name = target.Replace('\\', '/').Split('/')[^1];
        var matches = paths.Where(path => path.Split('/')[^1] == name).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? Normalize(string value)
    {
        var parts = new List<string>();
        foreach (var part in value.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
