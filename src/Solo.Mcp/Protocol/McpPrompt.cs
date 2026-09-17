using System.Text.Json.Nodes;
using Solo.Mcp.Rest;

namespace Solo.Mcp.Protocol;

/// <summary>
/// One MCP prompt: a workflow guide a client can offer the user, usually as a
/// slash command.
/// </summary>
/// <remarks>
/// Prompts carry the guidance that tool descriptions cannot. A tool description
/// is paid for on every request and has to stay one line; a prompt is fetched
/// only when it is used, so it can be the whole procedure — which board to file
/// on, which order to move the card in, what the data does not record.
/// </remarks>
public sealed record McpPrompt(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<McpPromptArgument> Arguments,
    string Body)
{
    /// <summary>
    /// The guide, with whatever the caller supplied appended as a closing
    /// section. Arguments are context, not a template: substituting them into
    /// the body would mean maintaining placeholders in prose that reads fine
    /// without them, and a client that passes nothing still gets a usable guide.
    /// </summary>
    public string Render(JsonObject? arguments)
    {
        var missing = Arguments
            .Where(a => a.Required && string.IsNullOrWhiteSpace(arguments?[a.Name]?.ToString()))
            .Select(a => a.Name)
            .ToList();
        if (missing.Count > 0)
        {
            throw new McpToolException($"\"{Name}\" requires {string.Join(", ", missing)}.");
        }

        var supplied = Arguments
            .Select(a => (a.Label, Value: arguments?[a.Name]?.ToString()))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToList();

        if (supplied.Count == 0)
        {
            return Body;
        }

        var lines = supplied.Select(x => $"- {x.Label}: {x.Value!.Trim()}");
        return $"{Body}\n\n---\n\n## This request\n\n{string.Join("\n", lines)}\n";
    }
}

/// <param name="Label">How the value is introduced in the rendered guide.</param>
public sealed record McpPromptArgument(string Name, string Label, string Description, bool Required);
