namespace Solo.DesignDocs.Contracts;

/// <summary>
/// What a vault, folder, note, sketch or image name may be, checked the same
/// way by the dialogs and by the server.
/// </summary>
/// <remarks>
/// The rule is Obsidian's, since notes have to survive a round trip through an
/// Obsidian vault: no path separators, and none of the characters a wiki link
/// uses for its own syntax (<c>[ ] # ^ |</c>), which would make a
/// <c>[[link]]</c> to the note impossible to write. <c>:</c> is out because
/// Windows and macOS reject it in file names.
/// </remarks>
public static class DocNames
{
    private static readonly char[] Forbidden = ['/', '\\', ':', '[', ']', '#', '^', '|', '*', '"', '<', '>', '?'];

    /// <summary>The name trimmed, or null with <paramref name="problem"/> saying what's wrong with it.</summary>
    public static string? Clean(string? name, out string? problem)
    {
        var trimmed = (name ?? "").Trim();
        problem = trimmed.Length == 0 ? "A name can't be empty."
            : trimmed.Length > DocsLimits.Name ? $"A name can be at most {DocsLimits.Name} characters."
            : trimmed is "." or ".." || trimmed.StartsWith('.') ? "A name can't start with a dot."
            : trimmed.IndexOfAny(Forbidden) >= 0 ? "A name can't contain / \\ : [ ] # ^ | * \" < > or ?."
            : trimmed.Any(char.IsControl) ? "A name can't contain control characters."
            : null;
        return problem is null ? trimmed : null;
    }

    /// <summary>A note name as typed, without a trailing <c>.md</c> someone added out of habit.</summary>
    public static string? CleanNote(string? name, out string? problem)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }

        return Clean(trimmed, out problem);
    }

    /// <summary>
    /// A vault-relative path, '/'-separated, each segment a valid name, or null.
    /// Leading and doubled slashes are dropped; <c>..</c> never resolves.
    /// </summary>
    public static string? CleanPath(string? path, out string? problem)
    {
        var segments = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        problem = null;
        if (segments.Length == 0)
        {
            problem = "A path can't be empty.";
            return null;
        }

        var cleaned = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (Clean(segment, out problem) is not { } clean)
            {
                return null;
            }

            cleaned.Add(clean);
        }

        var joined = string.Join('/', cleaned);
        if (joined.Length > DocsLimits.Path)
        {
            problem = $"A path can be at most {DocsLimits.Path} characters.";
            return null;
        }

        return joined;
    }
}
