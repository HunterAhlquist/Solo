using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Layout;
using Solo.Client.Pages;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// An instance with a display name of its own goes by it wherever the app
/// names itself, and says it runs Solo in the one place that always does.
/// </summary>
public class InstanceBrandingTests : ComponentTestBase
{
    private static AppConfig Config(string? displayName, string? tagline = null) => new()
    {
        Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true)
        {
            DisplayName = displayName,
            Tagline = tagline,
        },
    };

    private IRenderedComponent<MainLayout> RenderLayout(string? displayName)
    {
        this.AddAuthorization();
        Services.AddSingleton(new UserSession(new StubHttp().CreateClient()));
        Services.AddSingleton(new StubHttp().CreateClient());
        Services.AddSingleton(Config(displayName));
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
        return Render<MainLayout>();
    }

    [Fact]
    public void A_named_instance_puts_its_name_in_the_app_bar_and_credits_Solo_in_the_footer()
    {
        var layout = RenderLayout("Acme Games");

        Assert.Equal("Acme Games", layout.Find(".app-brand").TextContent.Trim());
        var footer = layout.Find(".app-footer__copyright").TextContent;
        Assert.Contains("Acme Games, powered by Solo.", footer);
        Assert.Contains("View Source on Github", footer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void An_unnamed_instance_goes_by_Solo(string? displayName)
    {
        var layout = RenderLayout(displayName);

        Assert.Equal("Solo", layout.Find(".app-brand").TextContent.Trim());
        var footer = layout.Find(".app-footer__copyright").TextContent;
        Assert.Contains("Solo, a simple project management app", footer);
        Assert.DoesNotContain("powered by", footer);
    }

    private IRenderedComponent<SignIn> RenderSignIn(AppConfig config)
    {
        Services.AddSingleton(config);
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
        return Render<SignIn>();
    }

    [Fact]
    public void The_sign_in_page_is_headed_by_the_instances_name_and_tagline()
    {
        var page = RenderSignIn(Config("Acme Games", "Where the anvils come from."));

        Assert.Equal("Acme Games", page.Find("h1").TextContent.Trim());
        Assert.Contains("Where the anvils come from.", page.Markup);
        Assert.DoesNotContain(InstanceBranding.DefaultTagline, page.Markup);
    }

    [Fact]
    public void An_instance_without_a_tagline_shows_the_default_one()
    {
        var page = RenderSignIn(Config("Acme Games"));

        Assert.Contains(InstanceBranding.DefaultTagline, page.Markup);
    }

    /// <summary>
    /// Saving a name reloads the page, so it waits for its button: leaving a
    /// field saves nothing. And the button saves the branding alone, with the
    /// other settings sent as they were last stored.
    /// </summary>
    [Fact]
    public void Branding_is_saved_by_its_button_and_not_by_leaving_a_field()
    {
        var http = new StubHttp()
            .RouteJson("api/settings", new InstanceSettingsDto(90, true, null, 1) { Tagline = "Old line." })
            .RouteJson("api/themes", new ThemeScanResultDto([], []))
            .RouteJson("api/extensions/admin", new ExtensionAdminDto([], []))
            .RouteJson("api/admin/users", new List<AdminUserDto>());
        var api = http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));

        var page = Render<InstanceSettings>();
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".mud-input-control input").Count));
        IElement Save() => page.FindAll("button").Single(b => b.TextContent.Contains("Save changes"));
        Assert.True(Save().HasAttribute("disabled"));

        var name = page.FindAll(".mud-input-control input")[0];
        name.Input("  Acme Games ");
        name.Blur();
        page.FindAll(".mud-input-control input")[1].Input("");

        Assert.DoesNotContain(http.Paths, path => path.StartsWith("PUT"));
        Assert.False(Save().HasAttribute("disabled"));

        Save().Click();

        page.WaitForAssertion(() => Assert.Contains("PUT /api/settings", http.Paths));
        var sent = System.Text.Json.JsonDocument.Parse(http.Bodies.Last()).RootElement;
        Assert.Equal("Acme Games", sent.GetProperty("displayName").GetString());
        Assert.Equal(string.Empty, sent.GetProperty("tagline").GetString());
        Assert.Equal(90, sent.GetProperty("auditRetentionDays").GetInt32());
    }
}
