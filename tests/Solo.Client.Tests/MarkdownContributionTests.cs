using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Client.Components;
using Solo.Client.Extensions;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

public sealed class MarkdownContributionTests : ComponentTestBase
{
    private readonly MarkdownProbe _probe = new();

    public MarkdownContributionTests()
    {
        Services.AddSingleton(_probe);
        Services.AddSingleton(new ExtensionCatalog
        {
            Loaded = [new LoadedExtension(new ClientExtensionDto("markdown", "Markdown", "1.0.0", ">=1.10 <2.0", "Fixture", []),
                Assembly.GetExecutingAssembly())],
        });
    }

    [Fact]
    public void Labels_use_precise_spans_and_escape_provider_text()
    {
        var rendered = Render<SoloMarkdown>(p => p.Add(c => c.Value,
            "[old](/fixture/one) and [other](/fixture/two) [keep](/unclaimed) ![image](/fixture/image) [bad](javascript:alert(1))"));
        rendered.WaitForAssertion(() => Assert.Contains("Current [title]", rendered.Markup));
        Assert.Equal("Current [title]", rendered.Find("a[href='/fixture/one']").TextContent);
        Assert.Equal("Current [title]", rendered.Find("a[href='/fixture/two']").TextContent);
        Assert.Equal("keep", rendered.Find("a[href='/unclaimed']").TextContent);
        Assert.Equal("image", rendered.Find("img").GetAttribute("alt"));
        Assert.DoesNotContain("javascript:", rendered.Markup);
        Assert.Equal(new[] { "/fixture/one", "/fixture/two" }, Assert.Single(_probe.Batches));
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("slow")]
    public void A_failed_or_slow_provider_leaves_the_written_label(string behavior)
    {
        _probe.Behavior = behavior;
        var rendered = Render<SoloMarkdown>(p => p.Add(c => c.Value, "[original](/fixture/one)"));
        rendered.WaitForAssertion(() => Assert.Equal("original", rendered.Find("a").TextContent), TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_action_uses_the_selection_and_the_dom_length_guard(bool fits)
    {
        JSInterop.Setup<string?>("solo.markdown.selection", _ => true).SetResult("selected");
        JSInterop.Setup<bool>("solo.markdown.insertAtCursor", _ => true).SetResult(fits);
        var project = Guid.NewGuid();
        var entity = Guid.NewGuid();
        var rendered = Render<MarkdownEditor>(p => p.Add(c => c.AlwaysEditing, true).Add(c => c.CanWrite, true)
            .Add(c => c.ProjectId, project).Add(c => c.EntityId, entity).Add(c => c.Surface, MarkdownSurface.Comment)
            .Add(c => c.MaxLength, 50));
        rendered.FindAll("button").Single(b => b.TextContent.Contains("Insert fixture")).Click();
        rendered.WaitForAssertion(() => Assert.NotNull(_probe.Chosen));
        Assert.Equal(new MarkdownEditorContext(project, MarkdownSurface.Comment, entity, "selected"), _probe.Chosen);
        var insertion = Assert.Single(JSInterop.Invocations["solo.markdown.insertAtCursor"]);
        Assert.Equal("inserted", insertion.Arguments[1]);
        Assert.Equal(50, insertion.Arguments[2]);
        Assert.Equal(!fits, Services.GetRequiredService<ISnackbar>().ShownSnackbars.Any());
    }
}

public sealed class MarkdownProbe
{
    public string? Behavior { get; set; }
    public List<IReadOnlyList<string>> Batches { get; } = [];
    public MarkdownEditorContext? Chosen { get; set; }
}

[MarkdownEditorAction]
public sealed class FixtureMarkdownAction(MarkdownProbe probe) : ISoloMarkdownEditorAction
{
    public ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<MarkdownEditorButton>>([new("insert", "Insert fixture")]);
    public Task<string?> OnChosenAsync(string buttonId, MarkdownEditorContext context, CancellationToken ct)
    {
        probe.Chosen = context;
        return Task.FromResult<string?>("inserted");
    }
}

[MarkdownLinkProvider]
public sealed class FixtureMarkdownLinks(MarkdownProbe probe) : ISoloMarkdownLinkProvider
{
    public bool Claims(string url) => url.StartsWith("/fixture/");
    public async ValueTask<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        probe.Batches.Add(urls);
        if (probe.Behavior == "throw") throw new InvalidOperationException("Fixture failure");
        if (probe.Behavior == "slow") await Task.Delay(TimeSpan.FromSeconds(5), ct);
        return urls.ToDictionary(u => u, _ => "Current [title]");
    }
}
