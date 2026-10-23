using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

/// <summary>The client half's Blazor parts, rendered against a stubbed API.</summary>
public sealed class DocsComponentTests : ComponentTestBase
{
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly StubHttp _http = new();
    private readonly FakeLiveTopics _live = new();

    public DocsComponentTests()
    {
        Services.AddSingleton<ISoloClientApi>(_http.CreateClient());
        Services.AddSingleton<ISoloLiveTopics>(_live);
    }

    private static ProjectSummaryDto Project => new(ProjectId, "Game", "GAME", null, null, Solo.Shared.ProjectRole.ReadWrite, false);

    private static VaultDto Vault(string name, bool closed = false) =>
        new(Guid.NewGuid(), ProjectId, name, closed, closed ? DateTimeOffset.UtcNow : null, DateTimeOffset.UtcNow, 1);

    [Fact]
    public void The_sidebar_lists_open_vaults_and_hides_creating_from_those_who_cant()
    {
        _http.RouteJson("/vaults", new VaultListDto(false, [Vault("Combat"), Vault("Old", closed: true)]));

        var nav = Render<DocsNavSection>(p => p.Add(s => s.Project, Project).Add(s => s.CanWrite, true));

        nav.WaitForAssertion(() => Assert.Contains("Combat", nav.Markup));
        Assert.DoesNotContain(">Old<", nav.Markup.Replace(" ", ""));
        Assert.Contains("Closed (1)", nav.Markup);
        Assert.DoesNotContain("New design doc", nav.Markup);
        Assert.Contains((ProjectId, DocsTopics.Vaults), _live.Held);
    }

    [Fact]
    public void A_super_admin_sees_new_design_doc()
    {
        _http.RouteJson("/vaults", new VaultListDto(true, []));

        var nav = Render<DocsNavSection>(p => p.Add(s => s.Project, Project));

        nav.WaitForAssertion(() => Assert.Contains("New design doc", nav.Markup));
    }

    [Fact]
    public void Linked_docs_lists_a_descriptions_doc_links_with_their_current_names()
    {
        var noteId = Guid.NewGuid();
        var url = $"/docs/note/{noteId:N}#dodge";
        _http.RouteJson("links/labels", new Dictionary<string, string> { [url] = "Combat › Dodge" });
        var task = TestData.Task() with { Description = $"See [old name]({url}) and [[not a link]] and [x](/docs/note/{noteId:N}#dodge)." };

        var linked = Render<LinkedDocs>(p => p.Add(l => l.Task, task));

        linked.WaitForAssertion(() => Assert.Contains("Combat › Dodge", linked.Markup));
        Assert.Single(linked.FindAll(".mud-chip"));
    }

    [Fact]
    public void A_task_without_doc_links_adds_nothing()
    {
        var linked = Render<LinkedDocs>(p => p.Add(l => l.Task, TestData.Task() with { Description = "Plain text" }));

        Assert.Equal(string.Empty, linked.Markup.Trim());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task The_link_provider_claims_note_links_and_caches_answers()
    {
        var url = $"/docs/note/{Guid.NewGuid():N}";
        _http.RouteJson("links/labels", new Dictionary<string, string> { [url] = "Combat" });
        var provider = new NoteLinkProvider(_http.CreateClient());

        Assert.True(provider.Claims(url));
        Assert.False(provider.Claims("/projects/GAME"));
        Assert.Equal("Combat", (await provider.LabelsAsync([url], default))[url]);
        Assert.Equal("Combat", (await provider.LabelsAsync([url], default))[url]);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public void Content_urls_carry_the_package_version()
    {
        var version = typeof(DocsApi).Assembly.GetName().Version!.ToString(3);

        Assert.Equal($"https://solo.test/api/extensions/design-docs/content/design-docs.js?v={version}",
            new DocsApi(_http.CreateClient()).Content("design-docs.js").ToString());
    }

    [Fact]
    public void Member_colors_step_by_the_golden_angle()
    {
        Assert.Equal(0, DocsColors.HueOf(0));
        Assert.Equal(137.508, DocsColors.HueOf(1), 3);
        Assert.Equal("oklch(0.58 0.14 275.016)", DocsColors.Css(2));
    }
}
