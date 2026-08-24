using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Mcp.Client;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// The MCP extension's page, rendered the way the host renders it: resolving
/// MudBlazor and <see cref="ISoloClientApi"/> from the app's own service
/// provider, with nothing of its own registered.
/// </summary>
/// <remarks>
/// That the page can be rendered at all with only host services present is half
/// the point — it is what an extension assembly gets when the router hands it a
/// route, and the reason <c>ISoloClientApi</c> exists.
/// </remarks>
public class McpAccessTests : ComponentTestBase
{
    private const string InfoPath = "api/ext/mcp/info";
    private const string TokensPath = "api/ext/mcp/tokens";
    private const string AuditPath = "api/ext/mcp/tokens/admin";

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

    /// <summary>
    /// The client half loaded but the server half is disabled — a failed
    /// migration does exactly this, and the page has to say so rather than offer
    /// a token form pointed at a 503.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void A_disabled_server_half_is_reported_instead_of_a_token_form(HttpStatusCode status)
    {
        _http.Route(InfoPath, status);
        var page = RenderPage();

        Assert.Contains("MCP package is installed", page.Markup);
        Assert.Contains("server is disabled", page.Markup);
        Assert.DoesNotContain("Generate token", page.Markup);
    }

    [Fact]
    public void An_account_with_no_token_is_offered_one()
    {
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, NoToken(), HttpMethod.Get);

        var page = RenderPage();

        Assert.Contains("Generate token", page.Markup);
        Assert.DoesNotContain("Regenerate", page.Markup);
    }

    [Fact]
    public void The_page_uses_the_shared_readable_width_without_inline_caps()
    {
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, NoToken(), HttpMethod.Get);

        var page = RenderPage();

        var container = page.Find(".mud-container-maxwidth-md");
        Assert.DoesNotContain("mud-container--gutters", container.ClassList);
        Assert.DoesNotContain("max-width:", page.Markup);
    }

    /// <summary>
    /// The value exists in exactly one response, so the page has to show it and
    /// put it straight into the snippets the user is about to copy.
    /// </summary>
    [Fact]
    public void Generating_shows_the_value_once_and_puts_it_in_the_snippets()
    {
        const string secret = "solomcp_abcdef0123456789";
        Installed();

        // The status endpoint answers differently either side of the mint,
        // exactly as the real one does — so the flag flips when the POST lands.
        var minted = false;
        _http.Route(TokensPath, HttpStatusCode.OK, () =>
        {
            minted = true;
            return new { token = Token(revoked: false), value = secret };
        }, HttpMethod.Post);
        _http.Route(TokensPath, HttpStatusCode.OK,
            () => minted ? WithToken() : NoToken(), HttpMethod.Get);

        var page = RenderPage();
        page.FindAll("button").First(b => b.TextContent.Contains("Generate token")).Click();

        Assert.Contains("Copy this token now", page.Markup);
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
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, WithToken(), HttpMethod.Get);

        var page = RenderPage();

        Assert.Contains("Regenerate", page.Markup);
        Assert.Contains("Revoke", page.Markup);
        // Nothing to show: the server kept only a hash, so the snippets say so.
        Assert.Contains("YOUR_TOKEN", page.Markup);
    }

    [Fact]
    public void The_endpoint_url_is_absolute_against_the_configured_instance()
    {
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, NoToken(), HttpMethod.Get);

        var page = RenderPage();

        // (The package's own stylesheet goes out through HeadContent, which
        // renders into the host's HeadOutlet rather than into this markup; the
        // integration suite asserts the content endpoint serves it.)
        Assert.Contains("https://solo.test/api/ext/mcp", page.Markup);
    }

    /// <summary>
    /// Claude Desktop launches local processes only — an entry with a
    /// <c>url</c> is dropped as invalid, which is exactly the config the
    /// generic HTTP snippet produces. It gets the mcp-remote bridge instead.
    /// </summary>
    [Fact]
    public void Claude_desktop_gets_a_stdio_bridge_rather_than_a_url_entry()
    {
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, NoToken(), HttpMethod.Get);

        var page = RenderPage();
        var desktop = Snippet(page, "Claude Desktop", "claude_desktop_config.json");

        Assert.Contains("\"command\": \"npx\"", desktop);
        Assert.Contains("mcp-remote", desktop);
        Assert.Contains("https://solo.test/api/ext/mcp", desktop);
        // A url/type entry is what Claude Desktop rejects; this one must not carry it.
        Assert.DoesNotContain("\"url\"", desktop);
        Assert.DoesNotContain("\"type\": \"http\"", desktop);
        // The credential goes through env, or a client that fails to escape
        // spaces in args splits the header in half.
        Assert.Contains("\"Authorization:${SOLO_TOKEN}\"", desktop);
        Assert.Contains("\"SOLO_TOKEN\": \"Bearer ", desktop);
    }

    /// <summary>The HTTP shape is still offered, for clients that do accept it.</summary>
    [Fact]
    public void Clients_that_speak_http_still_get_a_url_entry()
    {
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, NoToken(), HttpMethod.Get);

        var page = RenderPage();
        var json = Snippet(page, "Cursor", "mcp.json");

        Assert.Contains("\"type\": \"http\"", json);
        Assert.Contains("\"url\": \"https://solo.test/api/ext/mcp\"", json);
    }

    /// <summary>
    /// The desktop shell serves the client from its own local origin and proxies
    /// the API onward, so the page's origin is a server that exists only while
    /// the app runs. What goes in an MCP client's config has to be the instance.
    /// </summary>
    [Fact]
    public async Task In_the_desktop_shell_the_snippets_name_the_instance_not_the_local_origin()
    {
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, NoToken(), HttpMethod.Get);

        // What the shell answers through the preload bridge.
        JSInterop.Setup<string?>("solo.desktop.instanceUrl").SetResult("https://solo.example.com");
        await _http.Instance.ResolveExternalAddressAsync(JSInterop.JSRuntime);

        var page = RenderPage();

        Assert.Contains("https://solo.example.com/api/ext/mcp", page.Markup);
        Assert.DoesNotContain("solo.test/api/ext/mcp", page.Markup);
    }

    /// <summary>
    /// One lost laptop should not cost everybody their token, so the audit
    /// offers a per-row revoke beside the instance-wide one.
    /// </summary>
    [Fact]
    public void An_admin_can_revoke_one_live_token_from_the_audit()
    {
        Installed(audit: AuditWith(revoked: false));
        _http.Route(TokensPath, HttpStatusCode.OK, WithToken(), HttpMethod.Get);

        var page = RenderPage();

        Assert.Single(page.FindAll("button[aria-label=\"Revoke Someone's token\"]"));
    }

    /// <summary>A revoked row is history; there is nothing left to cut off.</summary>
    [Fact]
    public void A_revoked_row_offers_no_revoke_button()
    {
        Installed(audit: AuditWith(revoked: true));
        _http.Route(TokensPath, HttpStatusCode.OK, NoToken(), HttpMethod.Get);

        var page = RenderPage();

        Assert.Empty(page.FindAll("button[aria-label=\"Revoke Someone's token\"]"));
        // The row is still listed — revocation is the audit trail, not a delete.
        Assert.Contains("Someone", page.Markup);
    }

    /// <summary>
    /// A 403 from the audit endpoint is how a normal user's page learns it has
    /// no admin section — there is no second endpoint to ask.
    /// </summary>
    [Fact]
    public void A_non_admin_sees_no_audit()
    {
        Installed();
        _http.Route(TokensPath, HttpStatusCode.OK, WithToken(), HttpMethod.Get);

        var page = RenderPage();

        Assert.DoesNotContain("All tokens", page.Markup);
        Assert.DoesNotContain("Revoke all", page.Markup);
    }

    [Fact]
    public void An_admin_sees_every_token_and_the_usage_graph_but_no_secret()
    {
        Installed(audit: AuditWith(revoked: false));
        _http.Route(TokensPath, HttpStatusCode.OK, WithToken(), HttpMethod.Get);

        var page = RenderPage();

        Assert.Contains("All tokens", page.Markup);
        // The owner's display name, and the label they gave the token.
        Assert.Contains("Someone", page.Markup);
        Assert.Contains("Laptop", page.Markup);
        Assert.Contains("Revoke all", page.Markup);
        // 24 dense hourly bars.
        Assert.Equal(24, page.FindAll(".mcp-usage__bar").Count);
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Registers the endpoints the page reads on load. Order matters: the audit
    /// path contains the tokens path, and the stub matches by fragment in
    /// registration order — so the more specific route goes in first, exactly as
    /// routing would resolve it server-side.
    /// </summary>
    /// <summary>
    /// The body of one titled snippet block, after opening the tab that holds
    /// it — MudTabs renders only the active panel, which is also all a user
    /// sees at once.
    /// </summary>
    private static string Snippet(IRenderedComponent<McpAccess> page, string tab, string title)
    {
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains(tab, StringComparison.OrdinalIgnoreCase))
            .Click();

        return page.FindAll(".mcp-snippet")
            .Single(block => block.QuerySelector(".mcp-snippet__header")!.TextContent.Contains(title))
            .QuerySelector("code")!
            .TextContent;
    }

    private void Installed(object? audit = null)
    {
        _http.RouteJson(InfoPath, Info);
        if (audit is null)
        {
            _http.Route(AuditPath, HttpStatusCode.Forbidden);
        }
        else
        {
            _http.Route(AuditPath, HttpStatusCode.OK, audit);
        }
    }

    private static object AuditWith(bool revoked) => new
    {
        windowStart = DateTimeOffset.UtcNow.AddHours(-23),
        tokens = new[]
        {
            new
            {
                id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                userEmail = "someone@example.com",
                userDisplayName = "Someone",
                description = "Laptop",
                createdAt = DateTimeOffset.UtcNow.AddDays(-2),
                lastUsedAt = (DateTimeOffset?)DateTimeOffset.UtcNow,
                revokedAt = revoked ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
                revokedReason = revoked ? "RevokedByAdmin" : null,
                usage = Enumerable.Range(0, 24).Select(i => i).ToArray(),
            },
        },
    };

    private static object NoToken() => new { active = (object?)null, history = Array.Empty<object>() };

    private static object WithToken() =>
        new { active = Token(revoked: false), history = new[] { Token(revoked: false) } };

    private static object Token(bool revoked) => new
    {
        id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        description = "Claude Code",
        createdAt = DateTimeOffset.UtcNow.AddDays(-1),
        lastUsedAt = (DateTimeOffset?)null,
        revokedAt = revoked ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        revokedReason = revoked ? "Regenerated" : null,
    };

    /// <summary>
    /// Only host services — the page brings none of its own, which is what an
    /// extension assembly actually gets.
    /// </summary>
    private IRenderedComponent<McpAccess> RenderPage()
    {
        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton<ISoloClientApi>(api);
        return Render<McpAccess>();
    }
}
