using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;

namespace Solo.Client.Tests;

/// <summary>
/// MudMarkdown renders every fenced code block through MudCodeHighlight, whose
/// first render calls <c>window.highlightCodeElement</c> — a function only the
/// package's own script defines. These tests run JS interop loose, so a host
/// page that never loads that script passes every component test and fails in
/// the browser instead: any description with a code fence threw an unhandled
/// render exception. This pairs what the component calls with what
/// <c>index.html</c> loads.
/// </summary>
public class MarkdownHostPageTests : BunitContext
{
    private const string MarkdownScript = "_content/MudBlazor.Markdown/MudBlazor.Markdown.min.js";

    public MarkdownHostPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();
    }

    [Fact]
    public void A_code_block_calls_into_a_script_the_host_page_loads()
    {
        Render<SoloMarkdown>(p => p.Add(x => x.Value, "```sql\nSELECT 1;\n```"));

        // If MudMarkdown ever stops calling this, the pairing below needs revisiting.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "highlightCodeElement");
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
