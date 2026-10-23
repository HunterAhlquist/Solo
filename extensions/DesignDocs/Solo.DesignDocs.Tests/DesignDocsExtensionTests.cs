using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;

namespace Solo.DesignDocs.Tests;

/// <summary>
/// The Design Docs extension through the running host: the package loads and
/// migrates its schema, its content is served, and every endpoint answers only
/// for the project its route names, with the project's own roles.
/// </summary>
/// <remarks>
/// The package is loaded from disk into its own AssemblyLoadContext; the shapes
/// here are the client half's copies of the same contract records, read as JSON.
/// </remarks>
[Collection("postgres")]
public sealed class DesignDocsExtensionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Base = "/api/ext/design-docs";

    private string _connectionString = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync("designdocs");
        _factory = new SoloApiFactory(_connectionString, [SoloApiFactory.PackageDirectory("design-docs")]);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    // -- The package -------------------------------------------------------------

    [SkippableFact]
    public async Task The_package_loads_and_migrates_its_own_schema()
    {
        postgres.SkipUnlessDockerAvailable();

        var admin = await _admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        var docs = Assert.Single(admin.Extensions, e => e.ExtensionId == "design-docs");
        Assert.Equal("Loaded", docs.State);
        Assert.Equal("ext_design_docs", docs.SchemaName);

        var client = Assert.Single(await _admin.GetJson<List<ClientExtensionDto>>("/api/extensions"), e => e.Id == "design-docs");
        Assert.Equal("Solo.DesignDocs.Client", client.ClientAssembly);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'ext_design_docs' ORDER BY 1", connection);
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["DocUpdates", "Folders", "Images", "MemberColors", "Notes", "Sketches", "Vaults", "__EFMigrationsHistory"], tables);
    }

    /// <summary>The bundle, its stylesheet and the pixel font load as ordinary same-origin files.</summary>
    [SkippableFact]
    public async Task Its_browser_module_is_served()
    {
        postgres.SkipUnlessDockerAvailable();
        var anonymous = _factory.CreateClient();

        var script = await anonymous.GetAsync("/api/extensions/design-docs/content/design-docs.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        var styles = await anonymous.GetAsync("/api/extensions/design-docs/content/design-docs.css");
        Assert.Equal("text/css", styles.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/extensions/design-docs/content/silkscreen.woff2")).StatusCode);

        // The page's CSP has no 'unsafe-eval'.
        var bundle = await script.Content.ReadAsStringAsync();
        Assert.DoesNotContain("eval(", bundle);
        Assert.DoesNotContain("new Function(", bundle);
    }

    // -- Vault lifecycle -------------------------------------------------------------

    /// <summary>Create, close, reopen and delete are the super admin's, even against the project's owner.</summary>
    [SkippableFact]
    public async Task Only_a_super_admin_manages_a_vaults_lifecycle()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Lifecycle", "DDLIFE");
        var vault = await VaultAsync(project, "Combat");
        var owner = await MemberAsync(project, "docs-owner", "ReadWrite", isOwner: true);
        var writer = await MemberAsync(project, "docs-writer", "ReadWrite");

        foreach (var client in new[] { owner, writer })
        {
            Assert.False((await client.GetJson<VaultListDto>($"{Base}/projects/{project.Id}/vaults")).CanManage);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync($"{Base}/projects/{project.Id}/vaults", new { name = "No" }, TestClients.Json)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(VaultUrl(project, vault) + "/close", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(VaultUrl(project, vault))).StatusCode);
        }

        Assert.True((await _admin.GetJson<VaultListDto>($"{Base}/projects/{project.Id}/vaults")).CanManage);

        // Open vaults can't be deleted: closing is the step that can be undone.
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.DeleteAsync(VaultUrl(project, vault))).StatusCode);
        var closed = await (await _admin.PostAsync(VaultUrl(project, vault) + "/close", null)).Content.ReadFromJsonAsync<VaultDto>(TestClients.Json);
        Assert.True(closed!.IsClosed);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync(VaultUrl(project, vault))).StatusCode);
        Assert.Empty((await _admin.GetJson<VaultListDto>($"{Base}/projects/{project.Id}/vaults")).Vaults);
    }

    /// <summary>The owner policy resolves the project, so a super admin can't act on one they aren't in.</summary>
    [SkippableFact]
    public async Task A_super_admin_outside_the_project_cant_manage_its_vaults()
    {
        postgres.SkipUnlessDockerAvailable();
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.PostAsJsonAsync(
            $"{Base}/projects/{Guid.NewGuid()}/vaults", new { name = "Nowhere" }, TestClients.Json)).StatusCode);
    }

    [SkippableFact]
    public async Task A_closed_vault_is_read_only_until_reopened()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Closing", "DDCLOSE");
        var vault = await VaultAsync(project, "Story");
        var note = await NoteAsync(project, vault, "Intro");
        (await _admin.PostAsync(VaultUrl(project, vault) + "/close", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsJsonAsync(VaultUrl(project, vault) + "/notes",
            new { name = "More" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PutAsJsonAsync(VaultUrl(project, vault) + $"/notes/{note.Id}/copy",
            new { markdown = "x", headings = Array.Empty<object>(), seq = 1 }, TestClients.Json)).StatusCode);

        // Reading still works.
        Assert.Single((await _admin.GetJson<VaultTreeDto>(VaultUrl(project, vault) + "/tree")).Notes);

        (await _admin.PostAsync(VaultUrl(project, vault) + "/reopen", null)).EnsureSuccessStatusCode();
        await NoteAsync(project, vault, "More");
    }

    // -- Roles and projects -------------------------------------------------------------

    [SkippableFact]
    public async Task A_read_member_can_read_but_not_write()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Reading", "DDREAD");
        var vault = await VaultAsync(project, "Lore");
        var note = await NoteAsync(project, vault, "Gods");
        var reader = await MemberAsync(project, "docs-reader", "Read");

        Assert.Single((await reader.GetJson<VaultTreeDto>(VaultUrl(project, vault) + "/tree")).Notes);
        Assert.Equal("Gods", (await reader.GetJson<NoteDto>(VaultUrl(project, vault) + $"/notes/{note.Id}")).Name);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(VaultUrl(project, vault) + "/notes",
            new { name = "Mine" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(VaultUrl(project, vault) + "/folders",
            new { name = "Mine" }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(VaultUrl(project, vault) + $"/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(VaultUrl(project, vault) + $"/notes/{note.Id}/copy",
            new { markdown = "x", headings = Array.Empty<object>(), seq = 1 }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(
            VaultUrl(project, vault) + "/images?path=a.png&width=1&height=1", Png())).StatusCode);
    }

    [SkippableFact]
    public async Task Ids_under_another_projects_route_are_404()
    {
        postgres.SkipUnlessDockerAvailable();
        var home = await ProjectAsync("Home", "DDHOME");
        var away = await ProjectAsync("Away", "DDAWAY");
        var vault = await VaultAsync(home, "Design");
        var note = await NoteAsync(home, vault, "Plan");
        var image = await ImageAsync(home, vault, "attachments/a.png");

        // The caller may write to both projects; the pairing is still wrong.
        var wrong = VaultUrl(away, vault);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(wrong + "/tree")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(wrong + $"/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(wrong + $"/images/{image.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(wrong + $"/documents/{note.Id}/log")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsJsonAsync(wrong + "/notes",
            new { name = "Planted" }, TestClients.Json)).StatusCode);
    }

    // -- Images ------------------------------------------------------------------------

    [SkippableFact]
    public async Task Images_go_only_to_members_and_never_render_as_a_page()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Pictures", "DDPICS");
        var vault = await VaultAsync(project, "Art");
        var image = await ImageAsync(project, vault, "attachments/hero.png");
        var url = VaultUrl(project, vault) + $"/images/{image.Id}";

        var response = await _admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        var stranger = _factory.As("stranger", "stranger@example.com");
        Assert.True((await stranger.GetAsync(url)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task An_upload_that_isnt_an_image_is_refused()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Uploads", "DDUPLD");
        var vault = await VaultAsync(project, "Art");
        var svg = new ByteArrayContent("<svg onload=\"alert(1)\"/>"u8.ToArray());
        svg.Headers.ContentType = new("image/svg+xml");

        var response = await _admin.PostAsync(VaultUrl(project, vault) + "/images?path=x.svg&width=1&height=1", svg);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Links ------------------------------------------------------------------------

    [SkippableFact]
    public async Task Links_resolve_only_for_people_who_can_read_the_note()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Linked", "DDLINK");
        var vault = await VaultAsync(project, "Design");
        var note = await NoteAsync(project, vault, "Combat");
        (await _admin.PutAsJsonAsync(VaultUrl(project, vault) + $"/notes/{note.Id}/copy", new
        {
            markdown = "# Dodge roll",
            headings = new[] { new { level = 1, text = "Dodge roll", slug = "dodge-roll" } },
            seq = 1,
        }, TestClients.Json)).EnsureSuccessStatusCode();

        var plain = $"/docs/note/{note.Id:N}";
        var heading = $"{plain}#dodge-roll";
        var labels = await _admin.PostJson<Dictionary<string, string>>($"{Base}/links/labels",
            new { urls = new[] { plain, heading, $"/docs/note/{Guid.NewGuid():N}" } });
        Assert.Equal("Combat", labels[plain]);
        Assert.Equal("Combat › Dodge roll", labels[heading]);
        Assert.Equal(2, labels.Count);

        var location = await _admin.GetJson<NoteLocationDto>($"{Base}/notes/{note.Id}/locate");
        Assert.Equal(("DDLINK", vault), (location.ProjectAlias, location.VaultId));

        // Someone outside the project learns nothing, not even that it exists.
        var stranger = await MemberAsync(await ProjectAsync("Theirs", "DDTHEIRS"), "outsider", "ReadWrite");
        Assert.Empty(await stranger.PostJson<Dictionary<string, string>>($"{Base}/links/labels", new { urls = new[] { plain } }));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Base}/notes/{note.Id}/locate")).StatusCode);
    }

    [SkippableFact]
    public async Task Task_lookups_give_the_current_serial_and_hide_other_projects_tasks()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Tasks", "DDTASK");
        var board = await _admin.PostJson<BoardDto>($"/api/projects/{project.Id}/boards", new { name = "Main", alias = "MAIN" });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{board.Id}/tasks", new { name = "Dodge roll" });

        var found = Assert.Single(await _admin.PostJson<List<TaskLookupDto>>($"{Base}/tasks/lookup", new { ids = new[] { task.Id } }));
        Assert.Equal(("DDTASK-MAIN-1", "Dodge roll", false), (found.Serial, found.Name, found.IsFinished));

        var stranger = _factory.As("task-stranger", "task-stranger@example.com");
        await MemberAsync(await ProjectAsync("Elsewhere", "DDELSE"), "task-stranger", "ReadWrite");
        Assert.Empty(await stranger.PostJson<List<TaskLookupDto>>($"{Base}/tasks/lookup", new { ids = new[] { task.Id } }));
    }

    // -- Import ------------------------------------------------------------------------

    [SkippableFact]
    public async Task Import_creates_folders_and_notes_and_skips_taken_names()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await ProjectAsync("Imported", "DDIMPORT");
        var vault = await VaultAsync(project, "Vault");
        await NoteAsync(project, vault, "Home");

        var result = await _admin.PostJson<ImportResultDto>(VaultUrl(project, vault) + "/import/notes", new
        {
            folders = new[] { "Design", "Design/Combat" },
            notes = new[]
            {
                new { path = "Home.md", markdown = "taken" },
                new { path = "Design/Combat/Dodge.md", markdown = "# Dodge\r\n" },
            },
        });

        Assert.Equal((2, 1), (result.FoldersCreated, result.NotesCreated));
        Assert.Single(result.Skipped);
        var tree = await _admin.GetJson<VaultTreeDto>(VaultUrl(project, vault) + "/tree");
        var dodge = Assert.Single(tree.Notes, n => n.Name == "Dodge");
        var log = await _admin.GetJson<DocumentLogDto>(VaultUrl(project, vault) + $"/documents/{dodge.Id}/log");
        Assert.Empty(log.Entries);
        Assert.Equal("# Dodge\r\n", log.SeedMarkdown);
    }

    // -- Helpers ------------------------------------------------------------------------

    private static string VaultUrl(ProjectDto project, Guid vault) => $"{Base}/projects/{project.Id}/vaults/{vault}";

    private Task<ProjectDto> ProjectAsync(string name, string alias) =>
        _admin.PostJson<ProjectDto>("/api/projects", new { name, alias });

    private async Task<Guid> VaultAsync(ProjectDto project, string name) =>
        (await _admin.PostJson<VaultDto>($"{Base}/projects/{project.Id}/vaults", new { name })).Id;

    private Task<NoteSummaryDto> NoteAsync(ProjectDto project, Guid vault, string name) =>
        _admin.PostJson<NoteSummaryDto>(VaultUrl(project, vault) + "/notes", new { name });

    private async Task<ImageSummaryDto> ImageAsync(ProjectDto project, Guid vault, string path)
    {
        var response = await _admin.PostAsync(VaultUrl(project, vault) + $"/images?path={Uri.EscapeDataString(path)}&width=1&height=1", Png());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImageSummaryDto>(TestClients.Json))!;
    }

    private async Task<HttpClient> MemberAsync(ProjectDto project, string sub, string role, bool isOwner = false)
    {
        var email = $"{sub}@example.com";
        var member = await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members", new { email, role });
        if (isOwner)
        {
            await _admin.PutJson<ProjectMemberDto>($"/api/projects/{project.Id}/members/{member.Id}", new { role, isOwner = true });
        }

        return _factory.As(sub, email);
    }

    /// <summary>The smallest PNG: one transparent pixel.</summary>
    internal static ByteArrayContent Png()
    {
        var content = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
        content.Headers.ContentType = new("image/png");
        return content;
    }
}
