using System.Text.Json.Nodes;
using Solo.Mcp.Rest;

namespace Solo.Mcp.Protocol;

public sealed partial class McpToolCatalog
{
    private static McpTool WorkflowTool(McpPromptCatalog prompts)
    {
        // Some clients expose only tools to the model. Derive this bridge from
        // the prompt catalog so both routes always serve the same instructions.
        var arguments = prompts.Prompts.SelectMany(p => p.Arguments)
            .DistinctBy(a => a.Name)
            .Select(a => (a.Name, Text(a.Description), false))
            .ToArray();

        return new McpTool("get_workflow", "Read a Solo workflow",
            "Read and follow this guide BEFORE acting: solo-recent-work for recent/finished work " +
            "recaps, solo-summarize-work for what to do next, solo-create-task for filing bugs or " +
            "requests, solo-implement-task for implementation (requires arguments.task). " +
            "Returns instructions only; then use Solo tools to carry out the workflow.",
            Schema(
                ("name", Choice("Workflow to follow", prompts.Prompts
                    .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name).ToArray()), true),
                ("arguments", Schema(arguments), false)),
            (args, _) =>
            {
                var name = RequireString(args, "name");
                var prompt = prompts.Find(name)
                    ?? throw new McpToolException($"Unknown workflow \"{name}\". Choose a name from get_workflow's schema.");
                if (args["arguments"] is not null and not JsonObject)
                {
                    throw new McpToolException("\"arguments\" must be an object of workflow arguments.");
                }

                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["name"] = prompt.Name,
                    ["instructions"] = prompt.Render(args["arguments"] as JsonObject),
                });
            });
    }
}
