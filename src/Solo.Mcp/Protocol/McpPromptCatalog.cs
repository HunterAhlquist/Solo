using System.Reflection;

namespace Solo.Mcp.Protocol;

/// <summary>
/// The workflow guides, read from the Markdown embedded in this assembly.
/// </summary>
/// <remarks>
/// <para>The files under <c>Skills/</c> are the single source. They are authored
/// as agent skills — YAML frontmatter naming the workflow and describing when to
/// reach for it, then the procedure — because that is also what a prompt needs:
/// <c>description</c> is what a client shows in its command list, and the body
/// is what the model reads. One file serves both, so the slash command and the
/// downloadable skill can never drift apart.</para>
///
/// <para>Embedded rather than shipped in <c>content/</c>: that folder is served
/// anonymously by design (the client fetches extension assets during bootstrap),
/// and a guide should reach the same people the tools do — holders of a token —
/// and no one else.</para>
/// </remarks>
public sealed class McpPromptCatalog
{
    private const string ResourcePrefix = "Solo.Mcp.Skills.";

    /// <summary>
    /// Arguments by prompt name. Kept here rather than in the frontmatter: they
    /// describe the MCP calling convention, which is this file's business, not
    /// something an agent skill has any notion of.
    /// </summary>
    private static readonly Dictionary<string, McpPromptArgument[]> Arguments = new(StringComparer.Ordinal)
    {
        ["solo-create-task"] =
        [
            new("report", "What was reported", "The bug report or request, in the reporter's own words.", false),
            new("project", "Project", "Project name or alias, when the reporter named one.", false),
        ],
        ["solo-implement-task"] =
        [
            new("task", "Task", "Serial number like \"DMD-PXW26-2\", or a task id.", true),
        ],
        ["solo-summarize-work"] =
        [
            new("project", "Project", "Project name or alias to walk.", false),
        ],
        ["solo-recent-work"] =
        [
            new("project", "Project", "Project name or alias to report on.", false),
            new("days", "Window", "Days to look back. Defaults to 14.", false),
        ],
    };

    private readonly IReadOnlyDictionary<string, McpPrompt> _prompts = Load();

    public IReadOnlyCollection<McpPrompt> Prompts => (IReadOnlyCollection<McpPrompt>)_prompts.Values;

    public McpPrompt? Find(string name) => _prompts.GetValueOrDefault(name);

    private static Dictionary<string, McpPrompt> Load()
    {
        var assembly = typeof(McpPromptCatalog).Assembly;
        var prompts = new Dictionary<string, McpPrompt>(StringComparer.Ordinal);

        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                                 n.EndsWith(".md", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            var (front, body) = Split(text);
            // A guide with no frontmatter is a packaging mistake, not a runtime
            // condition to degrade around: fall back to the file name so it is
            // still reachable, and so the gap is visible in prompts/list.
            var name = front.GetValueOrDefault("name")
                       ?? Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);

            prompts[name] = new McpPrompt(
                name,
                Title(name),
                front.GetValueOrDefault("description") ?? name,
                Arguments.GetValueOrDefault(name, []),
                body);
        }

        return prompts;
    }

    /// <summary>
    /// Frontmatter is a fixed shape this build writes and reads — four files,
    /// two scalar keys, no nesting — so it is parsed directly rather than
    /// carrying a YAML dependency into an extension package for it.
    /// </summary>
    private static (Dictionary<string, string> Front, string Body) Split(string text)
    {
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (front, normalized.Trim());
        }

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return (front, normalized.Trim());
        }

        foreach (var line in normalized[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                front[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var bodyStart = normalized.IndexOf('\n', end + 1);
        return (front, bodyStart < 0 ? string.Empty : normalized[bodyStart..].Trim());
    }

    /// <summary>"solo-create-task" → "Solo create task", for clients that show a title.</summary>
    private static string Title(string name)
    {
        var words = name.Replace('-', ' ').Replace('_', ' ');
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
