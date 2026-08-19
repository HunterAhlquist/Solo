using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Client.Pages;

namespace Solo.Client.Tests;

/// <summary>
/// The MCP access page. It is core UI over an extension's API, so the case that
/// matters most is the one where the package is not installed: the page has to
/// say so rather than render a token form pointed at a 404.
/// </summary>
public class McpAccessTests : ComponentTestBase
{
    private const string InfoPath = "api/ext/mcp/info";
    private const string TokensPath = "api/ext/mcp/tokens";

    private readonly StubHttp _http = new();

    private static object Info => new
    {
        protocolVersion = "2025-06-18",
        serverVersion = "1.0.0",
        tools = new[]
        {
            new { name = "list_projects", title = "List projects", description = "Lists every Solo project." },
        },
    };

    [Fact]
    public void An_instance_without_the_extension_says_so_instead_of_offering_a_token()
    {
        _http.Route(InfoPath, HttpStatusCode.NotFound);
        var page = RenderPage();

        Assert.Contains("isn't installed on this instance", page.Markup);
        Assert.DoesNotContain("Generate token", page.Markup);
    }

    /// <summary>
    /// A failed migration disables an extension and its routes answer 503. That
    /// is still "no MCP here" as far as this page is concerned; the reason lives
    /// in Admin settings.
    /// </summary>
    [Fact]
    public void A_disabled_extension_is_treated_the_same_as_an_absent_one()
    {
        _http.Route(InfoPath, HttpStatusCode.ServiceUnavailable);
        var page = RenderPage();

        Assert.Contains("isn't installed on this instance", page.Markup);
    }

    [Fact]
    public void An_account_with_no_token_is_offered_one()
    {
        _http.RouteJson(InfoPath, Info);
        _http.Route(TokensPath, HttpStatusCode.OK, new { active = (object?)null, history = Array.Empty<object>() });

        var page = RenderPage();

        Assert.Contains("Generate token", page.Markup);
        Assert.DoesNotContain("Regenerate", page.Markup);
    }

    /// <summary>
    /// The value exists in exactly one response, so the page has to show it and
    /// put it straight into the snippets the user is about to copy.
    /// </summary>
    [Fact]
    public void Generating_shows_the_value_once_and_puts_it_in_the_snippets()
    {
        const string secret = "solomcp_abcdef0123456789";
        _http.RouteJson(InfoPath, Info);

        // The status endpoint answers differently either side of the mint,
        // exactly as the real one does — so the flag flips when the POST lands.
        var minted = false;
        _http.Route(TokensPath, HttpStatusCode.OK, () =>
        {
            minted = true;
            return new { token = Token(revoked: false), value = secret };
        }, HttpMethod.Post);
        _http.Route(TokensPath, HttpStatusCode.OK, () => minted
            ? new { active = Token(revoked: false), history = new[] { Token(revoked: false) } }
            : new { active = (object?)null, history = Array.Empty<object>() },
            HttpMethod.Get);

        var page = RenderPage();
        page.FindAll("button").First(b => b.TextContent.Contains("Generate token")).Click();

        Assert.Contains("Copy this now", page.Markup);
        Assert.Contains(secret, page.Markup);
        // And the snippets carry it, rather than the placeholder.
        Assert.Contains($"Authorization: Bearer {secret}", page.Markup);
        Assert.DoesNotContain("YOUR_TOKEN", page.Markup);
        // The account now holds a token, so the page offers rotation.
        Assert.Contains("Regenerate", page.Markup);
    }

    [Fact]
    public void An_existing_token_offers_rotation_and_revocation_but_not_its_value()
    {
        _http.RouteJson(InfoPath, Info);
        _http.Route(TokensPath, HttpStatusCode.OK, new
        {
            active = Token(revoked: false),
            history = new[] { Token(revoked: false) },
        });

        var page = RenderPage();

        Assert.Contains("Regenerate", page.Markup);
        Assert.Contains("Revoke", page.Markup);
        // Nothing to show: the server kept only a hash, so the snippets say so.
        Assert.Contains("YOUR_TOKEN", page.Markup);
    }

    [Fact]
    public void The_endpoint_url_is_absolute_against_the_configured_instance()
    {
        _http.RouteJson(InfoPath, Info);
        _http.Route(TokensPath, HttpStatusCode.OK, new { active = (object?)null, history = Array.Empty<object>() });

        var page = RenderPage();

        Assert.Contains("https://solo.test/api/ext/mcp", page.Markup);
    }

    private static object Token(bool revoked) => new
    {
        id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        description = "Claude Code",
        createdAt = DateTimeOffset.UtcNow.AddDays(-1),
        lastUsedAt = (DateTimeOffset?)null,
        revokedAt = revoked ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        revokedReason = revoked ? "Regenerated" : null,
    };

    private IRenderedComponent<McpAccess> RenderPage()
    {
        Services.AddSingleton(_http.CreateClient());
        Services.AddSingleton(new InstanceConnection("https://solo.test/"));
        return Render<McpAccess>();
    }
}
