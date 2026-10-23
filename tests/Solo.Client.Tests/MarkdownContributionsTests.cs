using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Components;
using Solo.Client.Extensions;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// The markdown seams of contract 1.10: an extension's buttons in core's editor
/// insert text at the cursor, and its link provider relabels its own links
/// wherever core renders markdown.
/// </summary>
public class MarkdownContributionsTests : ComponentTestBase
{
    private static readonly Guid Project = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Entity = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly MarkdownTestControl _control = new();

    public MarkdownContributionsTests()
    {
        Services.AddSingleton(new ExtensionCatalog
        {
            Loaded =
            [
                new LoadedExtension(
                    new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.10 <2.0", "Fixture", []),
                    Assembly.GetExecutingAssembly()),
            ],
        });
        Services.AddSingleton(_control);
    }

    // -- Editor actions ----------------------------------------------------------

    private IRenderedComponent<MarkdownEditor> RenderEditor(int maxLength = 500) =>
        Render<MarkdownEditor>(p => p
            .Add(c => c.CanWrite, true)
            .Add(c => c.AlwaysEditing, true)
            .Add(c => c.MaxLength, maxLength)
            .Add(c => c.ProjectId, Project)
            .Add(c => c.EntityId, Entity)
            .Add(c => c.Surface, MarkdownSurface.TaskDescription));

    [Fact]
    public void A_contributed_button_inserts_its_text_at_the_cursor()
    {
        JSInterop.Setup<string?>("solo.markdown.selectedText", _ => true).SetResult("chosen words");
        var insert = JSInterop.Setup<string>("solo.markdown.insertAtCursor", _ => true);
        insert.SetResult("ok");
        var editor = RenderEditor(maxLength: 700);

        editor.WaitForAssertion(() => Assert.Contains("Link a thing", editor.Markup));
        editor.FindAll("button").First(b => b.TextContent.Contains("Link a thing")).Click();

        editor.WaitForAssertion(() => Assert.Single(insert.Invocations));
        var arguments = insert.Invocations.Single().Arguments;
        Assert.Equal("[chosen words](/test-docs/1)", arguments[1]);
        Assert.Equal(700, arguments[2]);

        var (buttonId, context) = Assert.Single(_control.Chosen);
        Assert.Equal("link", buttonId);
        Assert.Equal(new MarkdownEditorContext(Project, MarkdownSurface.TaskDescription, Entity, "chosen words"), context);
    }

    [Fact]
    public void An_insert_that_would_overflow_the_field_is_refused_with_a_message()
    {
        JSInterop.Setup<string>("solo.markdown.insertAtCursor", _ => true).SetResult("too-long");
        var editor = RenderEditor(maxLength: 10);

        editor.WaitForAssertion(() => Assert.Contains("Link a thing", editor.Markup));
        editor.FindAll("button").First(b => b.TextContent.Contains("Link a thing")).Click();

        editor.WaitForAssertion(() =>
            Assert.Contains(Services.GetRequiredService<MudBlazor.ISnackbar>().ShownSnackbars,
                s => s.Message.Contains("at most 10 characters")));
    }

    [Fact]
    public void An_editor_without_a_project_offers_no_extension_buttons()
    {
        var editor = Render<MarkdownEditor>(p => p.Add(c => c.CanWrite, true).Add(c => c.AlwaysEditing, true));

        Assert.DoesNotContain("Link a thing", editor.Markup);
        Assert.Empty(_control.Created);
    }

    [Fact]
    public void A_throwing_action_is_left_out_and_the_rest_still_show()
    {
        _control.ThrowOnButtons = true;
        var editor = RenderEditor();

        editor.WaitForAssertion(() => Assert.Contains("Link a thing", editor.Markup));
        Assert.DoesNotContain("Broken", editor.Markup);
    }

    // -- Link labels -------------------------------------------------------------

    private string Rendered(string markdown)
    {
        var component = Render<SoloMarkdown>(p => p.Add(c => c.Value, markdown));
        return component.Markup;
    }

    [Fact]
    public void A_claimed_link_shows_the_providers_label()
    {
        _control.Labels["/test-docs/1"] = "Combat design › Dodge";

        var component = Render<SoloMarkdown>(p => p.Add(c => c.Value, "See [old name](/test-docs/1) for more."));

        component.WaitForAssertion(() => Assert.Contains("Combat design › Dodge", component.Markup));
        Assert.DoesNotContain("old name", component.Markup);
        Assert.Contains("href=\"/test-docs/1\"", component.Markup);
    }

    [Fact]
    public void Unclaimed_links_images_and_unsafe_urls_are_left_alone()
    {
        _control.Labels["/elsewhere"] = "nope";
        _control.Labels["/test-docs/img"] = "nope";
        _control.Labels["javascript:/test-docs/x"] = "nope";

        var markup = Rendered("[mine](/elsewhere) ![pic](/test-docs/img) [bad](javascript:/test-docs/x)");

        Assert.Contains("mine", markup);
        Assert.Contains("alt=\"pic\"", markup);
        Assert.Contains("bad", markup);
        Assert.DoesNotContain("nope", markup);
        // Only what the provider claims reaches it, and nothing unsafe at all.
        Assert.All(_control.Asked, url => Assert.StartsWith("/test-docs/", url));
        Assert.DoesNotContain("/test-docs/img", _control.Asked);
    }

    [Fact]
    public void A_label_is_text_not_markup()
    {
        _control.Labels["/test-docs/1"] = "**bold** [x](javascript:alert(1)) <b>";

        var component = Render<SoloMarkdown>(p => p.Add(c => c.Value, "[name](/test-docs/1)"));

        component.WaitForAssertion(() => Assert.Contains("**bold**", component.Markup));
        Assert.DoesNotContain("<strong>", component.Markup);
        Assert.DoesNotContain("javascript:alert", component.Find("a").GetAttribute("href"));
        Assert.Contains("&lt;b&gt;", component.Markup);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("slow")]
    public void A_failing_provider_leaves_the_text_as_written(string failure)
    {
        _control.Labels["/test-docs/1"] = "New";
        _control.Failure = failure;

        var component = Render<SoloMarkdown>(p => p.Add(c => c.Value, "[As written](/test-docs/1)"));

        // The first render is the text as written, and the failed answer changes nothing.
        Assert.Contains("As written", component.Markup);
        Thread.Sleep(failure == "slow" ? 1300 : 100);
        component.Render();
        Assert.Contains("As written", component.Markup);
        Assert.DoesNotContain(">New<", component.Markup);
    }

    [Fact]
    public void Link_labels_are_located_in_the_source_exactly()
    {
        const string markdown = "a [one](/x) b [**two**](/y \"title\") ![img](/z) [ref][r]\n\n[r]: /w";

        var links = MarkdownContributions.ClaimableLinks(markdown);

        Assert.Equal(["one", "**two**", "ref"], links.Select(l => markdown[l.LabelStart..(l.LabelEnd + 1)]));
        Assert.Equal(["/x", "/y", "/w"], links.Select(l => l.Url));
    }
}

/// <summary>What the test contributions below were asked and should answer.</summary>
public sealed class MarkdownTestControl
{
    public Dictionary<string, string> Labels { get; } = [];
    public List<string> Asked { get; } = [];
    public List<(string ButtonId, MarkdownEditorContext Context)> Chosen { get; } = [];
    public List<object> Created { get; } = [];
    public bool ThrowOnButtons { get; set; }
    public string? Failure { get; set; }
}

[MarkdownEditorAction]
public sealed class TestLinkAction(MarkdownTestControl control) : ISoloMarkdownEditorAction
{
    public ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct)
    {
        control.Created.Add(this);
        return ValueTask.FromResult<IReadOnlyList<MarkdownEditorButton>>([new MarkdownEditorButton("link", "Link a thing")]);
    }

    public Task<string?> OnChosenAsync(string buttonId, MarkdownEditorContext context, CancellationToken ct)
    {
        control.Chosen.Add((buttonId, context));
        return Task.FromResult<string?>($"[{context.SelectedText ?? "thing"}](/test-docs/1)");
    }
}

[MarkdownEditorAction(Order = 1)]
public sealed class TestBrokenAction(MarkdownTestControl control) : ISoloMarkdownEditorAction
{
    public ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct) =>
        control.ThrowOnButtons
            ? throw new InvalidOperationException("broken")
            : ValueTask.FromResult<IReadOnlyList<MarkdownEditorButton>>([]);

    public Task<string?> OnChosenAsync(string buttonId, MarkdownEditorContext context, CancellationToken ct) =>
        Task.FromResult<string?>("Broken");
}

[MarkdownLinkProvider]
public sealed class TestLinkProvider(MarkdownTestControl control) : ISoloMarkdownLinkProvider
{
    public bool Claims(string url) => url.StartsWith("/test-docs/", StringComparison.Ordinal);

    public async ValueTask<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        control.Asked.AddRange(urls);
        switch (control.Failure)
        {
            case "throw":
                throw new InvalidOperationException("provider failed");
            case "slow":
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
                break;
        }

        return urls.Where(control.Labels.ContainsKey).ToDictionary(u => u, u => control.Labels[u]);
    }
}
