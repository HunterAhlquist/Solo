using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

/// <summary>
/// The text formatting palette, and what the ink palette does while text is
/// being edited. The sketch reports the selection's formatting; everything here
/// is what the dialog makes of it.
/// </summary>
/// <remarks>
/// The ink rules are the subtle part. A swatch means "the text is this colour",
/// so a selection spanning two colours must light none rather than pick one —
/// and clearing that selection has to bring a swatch back, or the palette is
/// left blank with no way to tell why.
/// </remarks>
public sealed class SketchTextFormatComponentTests : ComponentTestBase
{
    private (IRenderedComponent<MudBlazor.MudDialogProvider> Dialogs,SketchDialog Sketch,BunitJSModuleInterop Module) Open()
    {
        var project = Guid.NewGuid();
        var id = Guid.NewGuid();
        var http = new StubHttp();
        var live = new FakeLiveTopics();
        http.RouteJson("api/users/me",
            new Solo.Shared.Dtos.UserDto(Guid.NewGuid(), "alex@example.test", "Alex", null, false));
        http.Route("colors", System.Net.HttpStatusCode.OK, () => new ColorDto(Guid.NewGuid(), 0), HttpMethod.Post);
        http.Route("colors", System.Net.HttpStatusCode.OK, () => Array.Empty<ColorDto>(), HttpMethod.Get);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());
        Services.AddSingleton<ISoloLiveTopics>(live);

        var module = JSInterop.SetupModule(_ => true);
        var sketch = module.SetupModule("mountSketch", _ => true);
        sketch.SetupVoid("command", _ => true).SetVoidResult();

        var dialogs = Render<MudBlazor.MudDialogProvider>();
        SketchDialog? instance = null;
        dialogs.InvokeAsync(() => Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<SketchDialog>("Map", new MudBlazor.DialogParameters<SketchDialog>
            {
                { x => x.ProjectId, project },
                { x => x.VaultId, Guid.NewGuid() },
                { x => x.Sketch, new SketchDto(id, Guid.NewGuid(), "Map", "A4", "portrait", "dots", null, null, 1) },
                { x => x.CanWrite, true },
            }));
        dialogs.WaitForState(() => live.Held.Any(h => h.Topic == DocTopics.Doc(id)));
        instance = dialogs.FindComponent<SketchDialog>().Instance;

        // The palettes are disabled until the sketch reports it can be written.
        dialogs.InvokeAsync(() => instance!.Writable(true));
        return (dialogs, instance!, sketch);
    }

    private static string Format(
        bool? bold = false, bool? italic = false, bool? underline = false,
        int? size = 16, string? ink = "theme", bool selected = true) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            bold, italic, underline, size, ink, selected,
        });

    private static bool Lit(IRenderedComponent<MudBlazor.MudDialogProvider> dialogs, string label) =>
        dialogs.FindAll($"[aria-label='{label}']").Any(e => e.ClassList.Contains("is-selected"));

    private static bool Sent(BunitJSModuleInterop module, params string[] arguments) =>
        module.Invocations.Any(i => i.Identifier == "command" &&
            arguments.All(argument => i.Arguments.Any(value => value as string == argument)));

    [Fact]
    public async Task The_panel_appears_only_while_a_text_box_is_open()
    {
        var (dialogs, sketch, _) = Open();
        Assert.Empty(dialogs.FindAll(".design-docs-sketch-text-palette"));

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format()));
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindAll(".design-docs-sketch-text-palette")));

        // Closing the editor reports null, and the panel has nothing to act on.
        await dialogs.InvokeAsync(() => sketch.TextFormat("null"));
        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".design-docs-sketch-text-palette")));
    }

    [Fact]
    public async Task A_style_the_selection_has_is_lit()
    {
        var (dialogs, sketch, _) = Open();

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(bold: true, italic: false)));

        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "Bold")));
        Assert.False(Lit(dialogs, "Italic"));
    }

    /// <summary>
    /// Half the selection is bold, so the button is neither lit nor plain. A
    /// two-state button would have to lie in one direction or the other.
    /// </summary>
    [Fact]
    public async Task A_style_only_part_of_the_selection_has_is_marked_mixed()
    {
        var (dialogs, sketch, _) = Open();

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(bold: null)));

        dialogs.WaitForAssertion(() =>
            Assert.Contains(dialogs.FindAll("[aria-label='Bold']"), e => e.ClassList.Contains("is-mixed")));
        Assert.False(Lit(dialogs, "Bold"));
    }

    [Fact]
    public async Task The_size_is_shown_and_a_mixed_size_reads_as_unknown()
    {
        var (dialogs, sketch, _) = Open();

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(size: 32)));
        dialogs.WaitForAssertion(() => Assert.Equal("32", dialogs.Find(".design-docs-text-size").TextContent));

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(size: null)));
        dialogs.WaitForAssertion(() => Assert.Equal("–", dialogs.Find(".design-docs-text-size").TextContent));
    }

    [Fact]
    public async Task Selecting_text_lights_the_colour_it_is_in()
    {
        var (dialogs, sketch, _) = Open();
        Assert.True(Lit(dialogs, "Theme ink"));

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(ink: "red")));

        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "red ink")));
        Assert.False(Lit(dialogs, "Theme ink"));
    }

    [Fact]
    public async Task A_selection_of_several_colours_lights_none()
    {
        var (dialogs, sketch, _) = Open();

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(ink: null)));

        dialogs.WaitForAssertion(() => Assert.Empty(
            dialogs.FindAll(".design-docs-swatches .is-selected")));
    }

    /// <summary>
    /// Clearing a mixed selection has to put a swatch back. The palette would
    /// otherwise stay blank with nothing to say why, and the next colour click
    /// would look like it had done nothing.
    /// </summary>
    [Fact]
    public async Task Clearing_a_mixed_selection_restores_the_last_definite_colour()
    {
        var (dialogs, sketch, _) = Open();

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(ink: "blue")));
        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "blue ink")));

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(ink: null)));
        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".design-docs-swatches .is-selected")));

        // Selection cleared: no ink to report, and not a range any more.
        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(ink: null, selected: false)));
        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "blue ink")));
    }

    /// <summary>
    /// The pen's colour and the text's are different things. Editing text must
    /// not leave the pen on whatever the last selection happened to be.
    /// </summary>
    [Fact]
    public async Task Closing_the_editor_gives_the_pen_its_own_colour_back()
    {
        var (dialogs, sketch, module) = Open();

        dialogs.Find("[aria-label='green ink']").Click();
        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "green ink")));

        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(ink: "red")));
        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "red ink")));

        await dialogs.InvokeAsync(() => sketch.TextFormat("null"));

        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "green ink")));
        Assert.False(Lit(dialogs, "red ink"));
    }

    /// <summary>
    /// While text is open a colour click paints the selection, so it must reach
    /// the sketch as an ink command and must not move the pen's own colour.
    /// </summary>
    [Fact]
    public async Task A_colour_click_while_editing_paints_the_selection()
    {
        var (dialogs, sketch, module) = Open();
        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(ink: "theme")));

        dialogs.Find("[aria-label='purple ink']").Click();

        dialogs.WaitForAssertion(() => Assert.True(Sent(module, "ink", "purple")));
    }

    [Fact]
    public async Task The_style_buttons_send_their_command()
    {
        var (dialogs, sketch, module) = Open();
        await dialogs.InvokeAsync(() => sketch.TextFormat(Format()));

        foreach (var (label, action) in new[] { ("Bold", "bold"), ("Italic", "italic"), ("Underline", "underline") })
        {
            dialogs.Find($"[aria-label='{label}']").Click();
            dialogs.WaitForAssertion(() => Assert.True(Sent(module, action)));
        }
    }

    [Fact]
    public async Task The_size_steppers_move_through_the_sizes()
    {
        var (dialogs, sketch, module) = Open();
        await dialogs.InvokeAsync(() => sketch.TextFormat(Format(size: 16)));

        dialogs.Find("[aria-label='Larger text']").Click();
        dialogs.WaitForAssertion(() => Assert.True(Sent(module, "textSize", "20")));

        dialogs.Find("[aria-label='Smaller text']").Click();
        dialogs.WaitForAssertion(() => Assert.True(Sent(module, "textSize", "14")));
    }

    /// <summary>
    /// Removing the box, not the selection. It lives on this panel because an
    /// empty text box draws nothing for the eraser to find and nothing for the
    /// marquee to catch, so without it there is no way to take one back.
    /// </summary>
    [Fact]
    public async Task The_delete_button_removes_the_whole_text_box()
    {
        var (dialogs, sketch, module) = Open();
        await dialogs.InvokeAsync(() => sketch.TextFormat(Format()));

        dialogs.Find("[aria-label='Delete text']").Click();

        dialogs.WaitForAssertion(() => Assert.True(Sent(module, "deleteText")));
    }
}
