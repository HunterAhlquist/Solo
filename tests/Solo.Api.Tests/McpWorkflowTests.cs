using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Solo.Api.Tests.Integration;

namespace Solo.Api.Tests;

/// <summary>
/// Drives the packaged protocol without a database or credentials. A client
/// exposing only tools must still be able to read the embedded workflow guides.
/// </summary>
public sealed class McpWorkflowTests : IDisposable
{
    private readonly AssemblyLoadContext _context = new("mcp-workflow-test", isCollectible: true);
    private readonly ServiceProvider _services;
    private readonly object _server;
    private readonly MethodInfo _handle;

    public McpWorkflowTests()
    {
        var assembly = _context.LoadFromAssemblyPath(Path.Combine(
            SoloApiFactory.PackageDirectory("mcp"), "server", "Solo.Mcp.dll"));
        var restType = assembly.GetType("Solo.Mcp.Rest.SoloRestClient", throwOnError: true)!;
        var serverType = assembly.GetType("Solo.Mcp.Protocol.McpServer", throwOnError: true)!;
        var services = new ServiceCollection().AddLogging();
        // Guide retrieval must never use REST. Unusable dependencies make an
        // accidental data fetch fail instead of reaching a live instance.
        services.AddSingleton(restType, Activator.CreateInstance(restType, new object?[4])!);
        services.AddSingleton(assembly.GetType("Solo.Mcp.Protocol.McpPromptCatalog", true)!);
        services.AddSingleton(assembly.GetType("Solo.Mcp.Protocol.McpToolCatalog", true)!);
        _services = services.BuildServiceProvider();
        _server = ActivatorUtilities.CreateInstance(_services, serverType);
        _handle = serverType.GetMethod("HandleAsync")!;
    }

    [Fact]
    public async Task Native_prompts_advertise_and_return_the_embedded_recent_work_guide()
    {
        var initialized = await Call("initialize");
        Assert.NotNull(initialized["capabilities"]!["prompts"]);
        var prompts = (await Call("prompts/list"))["prompts"]!.AsArray();
        Assert.Equal(4, prompts.Count);
        Assert.Contains(prompts, p => p!["name"]!.GetValue<string>() == "solo-recent-work");
        var result = await Call("prompts/get", new JsonObject { ["name"] = "solo-recent-work" });
        Assert.Equal("user", result["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Contains("# Recent work recap", result["messages"]![0]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_tools_only_client_can_discover_and_fetch_the_recent_work_guide()
    {
        var initialized = await Call("initialize");
        Assert.Contains("call get_workflow", initialized["instructions"]!.GetValue<string>());
        var tools = (await Call("tools/list"))["tools"]!.AsArray();
        var workflow = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "get_workflow")!;
        Assert.Contains("recent", workflow["description"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("solo-recent-work", workflow["inputSchema"]!.ToJsonString());

        var result = await Call("tools/call", new JsonObject
        {
            ["name"] = "get_workflow",
            ["arguments"] = new JsonObject { ["name"] = "solo-recent-work" },
        });
        Assert.Null(result["isError"]);
        var guide = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
        var instructions = guide["instructions"]!.GetValue<string>();
        Assert.Contains("# Recent work recap", instructions);
        Assert.Contains("## Group by member", instructions);
        Assert.Contains("## Render it", instructions);
        Assert.Contains("Solo records no completion timestamp.", instructions);
    }

    [Theory]
    [InlineData("solo-create-task", "report", "A button does not respond")]
    [InlineData("solo-implement-task", "task", "DMD-PXW26-2")]
    [InlineData("solo-summarize-work", "project", "Solo")]
    [InlineData("solo-recent-work", "days", "7")]
    public async Task The_tool_returns_the_same_guide_and_request_context_as_the_prompt(
        string name, string argument, string value)
    {
        var parameters = new JsonObject
        {
            ["name"] = name, ["arguments"] = new JsonObject { [argument] = value },
        };
        var prompt = await Call("prompts/get", parameters.DeepClone().AsObject());
        var result = await Call("tools/call", new JsonObject
        {
            ["name"] = "get_workflow", ["arguments"] = parameters,
        });
        Assert.Null(result["isError"]);
        var guide = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(name, guide["name"]!.GetValue<string>());
        var instructions = guide["instructions"]!.GetValue<string>();
        Assert.Equal(prompt["messages"]![0]!["content"]!["text"]!.GetValue<string>(), instructions);
        Assert.Contains("## This request", instructions);
        Assert.Contains(value, instructions);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("not-a-workflow", "Unknown workflow")]
    [InlineData("solo-implement-task", "requires task")]
    public async Task Invalid_workflow_requests_return_actionable_tool_errors(string? name, string expected)
    {
        var result = await Call("tools/call", new JsonObject
        {
            ["name"] = "get_workflow", ["arguments"] = new JsonObject { ["name"] = name },
        });
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains(expected, result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("[]", "must be an object")]
    [InlineData("{\"task\":\" \"}", "requires task")]
    public async Task Malformed_or_empty_arguments_return_tool_errors(string arguments, string expected)
    {
        var result = await Call("tools/call", new JsonObject
        {
            ["name"] = "get_workflow",
            ["arguments"] = new JsonObject
            {
                ["name"] = "solo-implement-task", ["arguments"] = JsonNode.Parse(arguments),
            },
        });
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains(expected, result["content"]![0]!["text"]!.GetValue<string>());
    }

    private async Task<JsonNode> Call(string method, JsonObject? parameters = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters,
        };
        var response = await (Task<JsonNode?>)_handle.Invoke(_server, [request, CancellationToken.None])!;
        Assert.NotNull(response);
        Assert.Null(response["error"]);
        return response["result"]!;
    }

    public void Dispose()
    {
        _services.Dispose();
        _context.Unload();
    }
}
