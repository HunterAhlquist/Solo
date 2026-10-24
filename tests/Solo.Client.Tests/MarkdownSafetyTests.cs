using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;

namespace Solo.Client.Tests;

/// <summary>
/// User markdown — task/epic descriptions and comments — is hostile input, and
/// MudMarkdown's stock pipeline renders raw HTML and <c>javascript:</c> URLs
/// verbatim. These assert the hardened pipeline in
/// <see cref="SoloMarkdownPipeline"/> keeps them out of the DOM while leaving
/// ordinary markdown alone. Comments only need read access to post, so this is
/// the boundary between a read-only member and every viewer's session token.
/// </summary>
public class MarkdownSafetyTests : BunitContext
{
    public MarkdownSafetyTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();
    }

    // The payload's own text survives — escaped — so these assert on the angle
    // brackets: the attack needs a real tag, and "&lt;img …&gt;" is just prose.
    [Theory]
    [InlineData("<script>alert(1)</script>", "script")]
    [InlineData("<img src=x onerror=alert(1)>", "img")]
    [InlineData("hello <b onmouseover=alert(1)>bold</b>", "b ")]
    [InlineData("<svg onload=alert(1)></svg>", "svg")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>", "iframe")]
    public void Raw_html_renders_as_text_rather_than_markup(string markdown, string tag)
    {
        var markup = RenderMarkdown(markdown);

        Assert.DoesNotContain($"<{tag}", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"&lt;{tag.TrimEnd()}", markup, StringComparison.OrdinalIgnoreCase);
    }

    // MudBlazor.Markdown 9.10 replaced highlight.js with highlighters written in
    // C#, which build each code block's markup themselves — so a fence's text
    // reaches the DOM through the package's escaping, not the pipeline's. One
    // fence per language it highlights, plus one it doesn't, each carrying tags
    // that would run if a highlighter passed them through, or that close the
    // <code> element early.
    [Theory]
    [InlineData("csharp")]
    [InlineData("razor")]
    [InlineData("html")]
    [InlineData("xml")]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("rust")]
    [InlineData("go")]
    [InlineData("kotlin")]
    [InlineData("sql")]
    [InlineData("")]
    public void A_code_fence_renders_its_text_rather_than_markup(string language)
    {
        var cut = Render<SoloMarkdown>(p => p.Add(x => x.Value,
            $"```{language}\n" +
            "var s = \"<img src=x onerror=alert(1)>\"; // <script>alert(2)</script>\n" +
            "<svg onload=alert(3)></svg> /* </code></pre><iframe src=x></iframe> */\n" +
            "```"));

        // Scoped to the block itself: its copy button carries an icon <svg> of
        // its own. A highlighter may wrap tokens in spans, and nothing else.
        var code = Assert.Single(cut.FindAll("pre code"));
        Assert.All(code.QuerySelectorAll("*"), e =>
        {
            Assert.Equal("SPAN", e.TagName);
            Assert.DoesNotContain(e.Attributes, a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains("<img src=x onerror=alert(1)>", code.TextContent, StringComparison.Ordinal);
        Assert.Contains("</code></pre><iframe src=x></iframe>", code.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("img, script, iframe"));
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](  javascript:alert(1))")]
    [InlineData("[click](vbscript:msgbox(1))")]
    [InlineData("[click](data:text/html;base64,PHN2Zz4=)")]
    [InlineData("[click](file:///etc/passwd)")]
    [InlineData("![alt](javascript:alert(1))")]
    [InlineData("[click][ref]\n\n[ref]: javascript:alert(1)")]
    public void A_scripted_destination_is_neutralised(string markdown)
    {
        var markup = RenderMarkdown(markdown);

        Assert.DoesNotContain("javascript:", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file://", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[docs](https://example.com/a?b=c#d)", "https://example.com/a?b=c#d")]
    [InlineData("[mail](mailto:a@b.com)", "mailto:a@b.com")]
    [InlineData("[here](#section)", "#section")]
    [InlineData("see ||DMD-PXW26-2||", "/projects/DMD/boards/PXW26?task=2")]
    public void An_ordinary_destination_survives(string markdown, string expectedHref)
    {
        Assert.Contains($"href=\"{expectedHref}\"", RenderMarkdown(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_markdown_still_renders_its_structure()
    {
        Assert.Contains("<table>", RenderMarkdown("| a | b |\n| - | - |\n| 1 | 2 |"), StringComparison.Ordinal);
        Assert.Contains("<blockquote>", RenderMarkdown("> quoted"), StringComparison.Ordinal);
        Assert.Contains("<b>bold</b>", RenderMarkdown("**bold**"), StringComparison.Ordinal);
        Assert.Contains("<code>", RenderMarkdown("`code`"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://example.com", true)]
    [InlineData("HTTPS://example.com", true)]
    [InlineData("mailto:a@b.com", true)]
    [InlineData("/projects/DMD", true)]
    [InlineData("#anchor", true)]
    [InlineData("relative/path", true)]
    // A colon after a path separator is part of the path, not a scheme.
    [InlineData("/a/b:c", true)]
    [InlineData("?q=a:b", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" javascript:alert(1) ", false)]
    [InlineData("data:text/html,<script>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    public void The_scheme_rule_is_an_allow_list(string? url, bool safe)
    {
        Assert.Equal(safe, SoloMarkdownPipeline.IsSafeUrl(url));
    }

    private string RenderMarkdown(string markdown) =>
        Render<SoloMarkdown>(p => p.Add(x => x.Value, markdown)).Markup;
}
