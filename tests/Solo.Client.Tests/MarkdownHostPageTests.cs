using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;
using Solo.Client.Testing;

namespace Solo.Client.Tests;

/// <summary>
/// MudMarkdown gives every fenced code block a copy button that calls
/// <c>MudBlazorMarkdown.copyTextToClipboard</c> — a function only the package's
/// own script defines. These tests run JS interop loose, so a host page that
/// never loads that script passes every component test and fails in the browser
/// instead. (Before 9.10 the block's first render also called the script's
/// <c>highlightCodeElement</c>, so a missing script broke every description with
/// a code fence.) This pairs what the component calls with what
/// <c>index.html</c> loads.
/// </summary>
public class MarkdownHostPageTests : SoloBunitContext
{
    private const string MarkdownScript = "_content/MudBlazor.Markdown/MudBlazor.Markdown.min.js";

    // SoloBunitContext rather than a bare BunitContext: the click opens the
    // button's tooltip, and the popover service behind it only disposes async.
    public MarkdownHostPageTests()
    {
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();
    }

    [Fact]
    public void A_code_blocks_copy_button_calls_into_a_script_the_host_page_loads()
    {
        var cut = Render<SoloMarkdown>(p => p.Add(x => x.Value, "```sql\nSELECT 1;\n```"));

        cut.Find(".mud-markdown-code-highlight-copybtn").Click();

        // If MudMarkdown ever stops calling this, the pairing below needs revisiting.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "MudBlazorMarkdown.copyTextToClipboard");
        Assert.Contains($"<script src=\"{MarkdownScript}\"></script>", IndexHtml());
    }

    private static string IndexHtml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Solo.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "src", "Solo.Client", "wwwroot", "index.html"));
    }
}
