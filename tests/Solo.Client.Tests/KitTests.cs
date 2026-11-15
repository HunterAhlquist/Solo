using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Kit;
using Solo.Shared.Kit;

namespace Solo.Client.Tests;

/// <summary>
/// The kit (<see cref="Solo.Shared.Kit"/>): components that say what a piece
/// of a contribution is, drawn by whoever hosts them — the web client with
/// MudBlazor, and anything without a host as plain HTML.
/// </summary>
public class KitTests : ComponentTestBase
{
    /// <summary>The kit components as the web client draws them.</summary>
    private void OnTheWeb() => Services.AddSingleton<ISoloKitRenderer, MudKitRenderer>();

    private static IEnumerable<Type> KitComponents() =>
        typeof(SoloKitComponent).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(SoloKitComponent)) && !type.IsAbstract);

    [Fact]
    public void A_button_is_core_s_button_and_its_click_reaches_the_extension()
    {
        OnTheWeb();
        var clicked = 0;
        var button = Render<SoloButton>(parameters => parameters
            .Add(b => b.Text, "Add to sprint")
            .Add(b => b.Variant, SoloButtonVariant.Primary)
            .Add(b => b.OnClick, () => clicked++));

        var drawn = button.Find("button.mud-button-root");
        Assert.Contains("mud-button-filled", drawn.ClassList);
        Assert.Contains("Add to sprint", drawn.TextContent);
        drawn.Click();
        Assert.Equal(1, clicked);
    }

    [Fact]
    public void A_button_with_somewhere_to_go_is_a_link_there()
    {
        OnTheWeb();
        var button = Render<SoloButton>(parameters => parameters
            .Add(b => b.Text, "Open board")
            .Add(b => b.Href, "projects/DMD/boards/main"));

        Assert.Equal("projects/DMD/boards/main", button.Find("a.mud-button-root").GetAttribute("href"));
    }

    [Fact]
    public void A_disabled_button_goes_nowhere()
    {
        OnTheWeb();
        var button = Render<SoloButton>(parameters => parameters
            .Add(b => b.Text, "Open board")
            .Add(b => b.Href, "projects/DMD/boards/main")
            .Add(b => b.Disabled, true));

        Assert.Empty(button.FindAll("a"));
        Assert.True(button.Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public void A_row_is_a_link_a_button_or_just_a_line_depending_on_what_it_was_given()
    {
        OnTheWeb();
        var pressed = 0;

        var link = Render<SoloRow>(parameters => parameters.Add(r => r.Title, "Roadmap").Add(r => r.Href, "roadmap/DMD"));
        Assert.Equal("roadmap/DMD", link.Find("a.solo-kit-row").GetAttribute("href"));

        var action = Render<SoloRow>(parameters => parameters.Add(r => r.Title, "Start").Add(r => r.OnClick, () => pressed++));
        var button = action.Find("div.solo-kit-row[role=button]");
        button.Click();
        button.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        button.KeyDown(new KeyboardEventArgs { Key = "a" });
        Assert.Equal(2, pressed);

        var line = Render<SoloRow>(parameters => parameters.Add(r => r.Title, "Nothing to do").Add(r => r.Subtitle, "Yet"));
        var plain = line.Find("div.solo-kit-row");
        Assert.False(plain.HasAttribute("role"));
        Assert.Equal("Yet", line.Find(".solo-kit-row__subtitle").TextContent);
    }

    [Fact]
    public void A_row_carries_what_follows_its_title()
    {
        OnTheWeb();
        var row = Render<SoloRow>(parameters => parameters
            .Add(r => r.Title, "Sprint 4")
            .AddChildContent<SoloBadge>(badge => badge.Add(b => b.Text, "3 left").Add(b => b.Tone, SoloTone.Warning)));

        var badge = row.Find(".solo-kit-row .mud-chip");
        Assert.Contains("3 left", badge.TextContent);
        Assert.Contains("mud-chip-color-warning", badge.ClassList);
    }

    [Fact]
    public void Text_takes_its_size_from_what_it_is_for_and_its_colour_from_its_tone()
    {
        OnTheWeb();
        var caption = Render<SoloText>(parameters => parameters
            .Add(t => t.Text, "No roadmaps.")
            .Add(t => t.Variant, SoloTextVariant.Caption)
            .Add(t => t.Tone, SoloTone.Muted));

        var drawn = caption.Find(".mud-typography");
        Assert.Contains("mud-typography-caption", drawn.ClassList);
        Assert.Contains("mud-secondary-text", drawn.ClassList);
        Assert.Equal("No roadmaps.", drawn.TextContent);
    }

    [Fact]
    public void Text_is_text_whatever_is_in_it()
    {
        OnTheWeb();
        var text = Render<SoloText>(parameters => parameters.Add(t => t.Text, "<b>bold</b>"));

        Assert.Empty(text.FindAll("b"));
        Assert.Equal("<b>bold</b>", text.Find(".mud-typography").TextContent);
    }

    [Fact]
    public void A_toggle_binds_like_a_checkbox()
    {
        OnTheWeb();
        bool? heard = null;
        var toggle = Render<SoloToggle>(parameters => parameters
            .Add(t => t.Label, "Work for an LLM")
            .Add(t => t.Value, false)
            .Add(t => t.ValueChanged, value => heard = value));

        Assert.Contains("Work for an LLM", toggle.Markup);
        toggle.Find("input").Change(true);
        Assert.True(heard);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(0.25, 25)]
    [InlineData(1.4, 100)]
    public void Progress_runs_from_nothing_to_done_and_no_further(double value, double percent)
    {
        OnTheWeb();
        var progress = Render<SoloProgress>(parameters => parameters.Add(p => p.Value, value).Add(p => p.Label, "3 of 8 done"));

        var bar = progress.Find("[role=progressbar]");
        Assert.Equal(percent.ToString(System.Globalization.CultureInfo.InvariantCulture), bar.GetAttribute("aria-valuenow"));
        Assert.Equal("3 of 8 done", bar.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_stack_lays_its_children_out_in_the_order_they_were_written()
    {
        OnTheWeb();
        var stack = Render<SoloStack>(parameters => parameters
            .Add(s => s.Direction, SoloDirection.Horizontal)
            .AddChildContent(builder =>
            {
                builder.OpenComponent<SoloText>(0);
                builder.AddComponentParameter(1, nameof(SoloText.Text), "One");
                builder.CloseComponent();
                builder.OpenComponent<SoloText>(2);
                builder.AddComponentParameter(3, nameof(SoloText.Text), "Two");
                builder.CloseComponent();
            }));

        Assert.Contains("flex-row", stack.Find(".solo-kit-stack").ClassList);
        Assert.Equal(["One", "Two"], stack.FindAll(".solo-kit-text").Select(text => text.TextContent));
    }

    [Fact]
    public void What_is_drawn_follows_the_component_when_its_parameters_change()
    {
        OnTheWeb();
        var badge = Render<SoloBadge>(parameters => parameters.Add(b => b.Text, "opened 1×"));

        badge.Render(parameters => parameters.Add(b => b.Text, "opened 2×"));

        Assert.Contains("opened 2×", badge.Find(".mud-chip").TextContent);
    }

    [Fact]
    public void Without_a_host_the_kit_is_plain_html_that_still_works()
    {
        var clicked = 0;
        bool? heard = null;

        var button = Render<SoloButton>(parameters => parameters.Add(b => b.Text, "Go").Add(b => b.OnClick, () => clicked++));
        var drawn = button.Find("button[data-solo-kit=button]");
        Assert.Equal("Go", drawn.TextContent);
        drawn.Click();
        Assert.Equal(1, clicked);

        var row = Render<SoloRow>(parameters => parameters.Add(r => r.Title, "Roadmap").Add(r => r.Href, "roadmap/DMD"));
        Assert.Equal("roadmap/DMD", row.Find("a[data-solo-kit=row]").GetAttribute("href"));

        var toggle = Render<SoloToggle>(parameters => parameters.Add(t => t.Label, "On").Add(t => t.ValueChanged, value => heard = value));
        toggle.Find("input[type=checkbox]").Change(true);
        Assert.True(heard);

        var text = Render<SoloText>(parameters => parameters.Add(t => t.Text, "<i>plain</i>"));
        Assert.Equal("<i>plain</i>", text.Find("[data-solo-kit=text]").TextContent);
    }

    [Fact]
    public void The_kit_is_closed()
    {
        // A kit component is a promise that every client can draw it, so nobody
        // outside the contract can add one or change what one is.
        Assert.All(KitComponents(), type => Assert.True(type.IsSealed, $"{type.Name} is not sealed"));
        Assert.DoesNotContain(
            typeof(SoloKitComponent).GetConstructors(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            constructor => constructor.IsPublic || constructor.IsFamily || constructor.IsFamilyOrAssembly);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_kit_component_has_a_drawing(bool onTheWeb)
    {
        // A component added to the kit without one would render as nothing, silently.
        if (onTheWeb)
        {
            OnTheWeb();
        }

        var components = KitComponents().ToList();
        Assert.NotEmpty(components);
        foreach (var type in components)
        {
            var rendered = Render(builder =>
            {
                builder.OpenComponent(0, type);
                builder.CloseComponent();
            });

            Assert.False(string.IsNullOrWhiteSpace(rendered.Markup), $"{type.Name} drew nothing {(onTheWeb ? "on the web" : "as plain HTML")}");
        }
    }
}
