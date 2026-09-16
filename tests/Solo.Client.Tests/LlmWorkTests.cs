using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Components;
using Solo.Client.Extensions;
using Solo.Mcp.Client;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

public sealed class LlmWorkTests : ComponentTestBase
{
    private readonly StubHttp _http = new();

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void The_host_loads_the_checkbox_only_for_eligible_tasks(bool eligible, bool canWrite, bool marked)
    {
        var task = TestData.Task();
        _http.RouteJson("llm-work", new { taskId = task.Id, eligible, llmWork = marked, assigneeIds = Array.Empty<Guid>(), version = 7 });
        Services.AddSingleton<ISoloClientApi>(_http.CreateClient());
        Services.AddSingleton(new ExtensionCatalog
        {
            Loaded = [new LoadedExtension(new ClientExtensionDto("mcp", "MCP", "1.2.0", ">=1.2 <2.0",
                "Solo.Mcp.Client", []), typeof(LlmWork).Assembly)],
        });
        var host = Render<TaskDetailExtensions>(p => p.Add(c => c.Task, task).Add(c => c.CanWrite, canWrite));
        if (!eligible) { Assert.Empty(host.FindAll("input[type=checkbox]")); return; }
        var input = host.Find("input[type=checkbox]");
        Assert.Equal(!canWrite, input.HasAttribute("disabled"));
        Assert.Equal(marked, input.HasAttribute("checked"));
        Assert.Contains("Work for an LLM", host.Markup);
    }

    [Fact]
    public void Saving_sends_the_loaded_version_and_refreshes_the_host()
    {
        var task = TestData.Task();
        _http.Route("llm-work", HttpStatusCode.OK,
            new { taskId = task.Id, eligible = true, llmWork = false, assigneeIds = Array.Empty<Guid>(), version = 7 }, HttpMethod.Get);
        _http.Route("llm-work", HttpStatusCode.OK,
            new { taskId = task.Id, eligible = true, llmWork = true, assigneeIds = Array.Empty<Guid>(), version = 8 }, HttpMethod.Put);
        Services.AddSingleton<ISoloClientApi>(_http.CreateClient());
        var refreshed = false;
        var component = Render<LlmWork>(p => p.Add(c => c.Task, task).Add(c => c.CanWrite, true)
            .Add(c => c.OnChanged, () => refreshed = true));
        component.Find("input[type=checkbox]").Change(true);
        component.WaitForAssertion(() => Assert.True(refreshed));
        Assert.Contains(_http.Bodies, body => body.Contains("\"version\":7") && body.Contains("\"llmWork\":true"));
    }

    [Fact]
    public void Uninstalling_the_extension_removes_the_slot_content()
    {
        Services.AddSingleton(new ExtensionCatalog());
        var host = Render<TaskDetailExtensions>(p => p.Add(c => c.Task, TestData.Task()));
        Assert.Empty(host.FindAll("input"));
    }
}
