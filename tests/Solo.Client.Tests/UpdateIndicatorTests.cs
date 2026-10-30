using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Components;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The app bar's server-update badge. It is deliberately the quietest thing on
/// the page: visible only to someone who can act on it, and only when there is
/// something to act on.
/// </summary>
public class UpdateIndicatorTests : ComponentTestBase
{
    private StubHttp _http = null!;

    private Task<IRenderedComponent<UpdateIndicator>> RenderAsync(
        bool superAdmin, object? updates)
    {
        _http = new StubHttp()
            .RouteJson("api/users/me", new UserDto(
                Guid.NewGuid(), "a@b.test", "Ada", null, superAdmin));

        if (updates is not null)
        {
            _http.RouteJson("api/updates", updates);
        }

        var api = _http.CreateClient();

        // Deliberately *not* pre-loaded. MainLayout hydrates the session after
        // its first render, so in the real app this component initializes while
        // IsSuperAdmin is still false; a test that hydrates first would pass
        // even if the indicator never appeared.
        var session = new UserSession(api);

        Services.AddSingleton(api);
        Services.AddSingleton(session);
        return Task.FromResult(Render<UpdateIndicator>());
    }

    private static UpdateStatusDto Available() => new(
        Enabled: true,
        DateTimeOffset.UtcNow,
        null,
        new ServerUpdateDto(
            "1.16.6+abc",
            "1.18.0",
            "https://github.com/HunterAhlquist/Solo/releases/tag/v1.18.0",
            "ghcr.io/hunterahlquist/solo:1.18.0",
            DateTimeOffset.UtcNow),
        []);

    [Fact]
    public async Task Links_a_super_admin_to_the_release()
    {
        var page = await RenderAsync(superAdmin: true, Available());

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("a")));
        var link = page.Find("a");
        Assert.Equal(
            "https://github.com/HunterAhlquist/Solo/releases/tag/v1.18.0",
            link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
    }

    /// <summary>
    /// Updating the server means pulling a new image, which only an operator can
    /// do. For anyone else this would be a badge they can neither act on nor
    /// dismiss — so it never renders, and the endpoint is never even called.
    /// </summary>
    [Fact]
    public async Task Shows_nothing_to_a_normal_user()
    {
        var page = await RenderAsync(superAdmin: false, Available());

        Assert.Empty(page.Markup.Trim());
        Assert.DoesNotContain(_http.Paths, path => path.Contains("api/updates"));
    }

    [Fact]
    public async Task Shows_nothing_when_the_instance_is_current()
    {
        var page = await RenderAsync(
            superAdmin: true,
            new UpdateStatusDto(Enabled: true, DateTimeOffset.UtcNow, null, null, []));

        Assert.Empty(page.Markup.Trim());
    }

    /// <summary>
    /// The app bar renders on every page, so a failed check must not throw
    /// through the layout — and there is no icon that honestly means "we could
    /// not tell". Admin settings reports the reason instead.
    /// </summary>
    [Fact]
    public async Task Shows_nothing_when_the_check_fails()
    {
        var page = await RenderAsync(superAdmin: true, updates: null);

        Assert.Empty(page.Markup.Trim());
    }
}
