using System.Net;
using System.Net.Http.Json;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The instance's branding — its display name and sign-in tagline: set by the
/// super admin, and readable by anyone, since the sign-in page and the desktop
/// app's instance list both show it before there is a session.
/// </summary>
[Collection("postgres")]
public sealed class InstanceBrandingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Settings = "/api/settings";

    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("display_name"));
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private Task<AuthConfigDto> AnonymousConfigAsync() =>
        _factory.CreateClient().GetJson<AuthConfigDto>("/api/config/auth");

    private async Task<string?> AnonymousNameAsync() => (await AnonymousConfigAsync()).DisplayName;

    [SkippableFact]
    public async Task TheName_IsUnsetByDefault_AndServedAnonymouslyOnceSet()
    {
        postgres.SkipUnlessDockerAvailable();

        var unset = await AnonymousConfigAsync();
        Assert.Null(unset.DisplayName);
        Assert.Null(unset.Tagline);

        var current = await _admin.GetJson<InstanceSettingsDto>(Settings);
        var saved = await _admin.PutJson<InstanceSettingsDto>(Settings, new
        {
            auditRetentionDays = current.AuditRetentionDays,
            displayName = "  Acme Games  ",
            tagline = " Where the anvils come from. ",
            version = current.Version,
        });

        Assert.Equal("Acme Games", saved.DisplayName);
        Assert.Equal("Where the anvils come from.", saved.Tagline);
        var served = await AnonymousConfigAsync();
        Assert.Equal("Acme Games", served.DisplayName);
        Assert.Equal("Where the anvils come from.", served.Tagline);
    }

    /// <summary>
    /// The desktop app bundles its own client, which can predate the field.
    /// Saving another setting from one must not take the name with it.
    /// </summary>
    [SkippableFact]
    public async Task AnUpdateThatOmitsTheName_LeavesIt_AndAnEmptyOneClearsIt()
    {
        postgres.SkipUnlessDockerAvailable();

        var current = await _admin.GetJson<InstanceSettingsDto>(Settings);
        current = await _admin.PutJson<InstanceSettingsDto>(Settings, new
        {
            auditRetentionDays = current.AuditRetentionDays,
            displayName = "Acme Games",
            tagline = "Where the anvils come from.",
            version = current.Version,
        });

        current = await _admin.PutJson<InstanceSettingsDto>(Settings, new
        {
            auditRetentionDays = 30,
            version = current.Version,
        });
        Assert.Equal(30, current.AuditRetentionDays);
        Assert.Equal("Acme Games", current.DisplayName);
        Assert.Equal("Where the anvils come from.", current.Tagline);

        // Each on its own: clearing one says nothing about the other.
        current = await _admin.PutJson<InstanceSettingsDto>(Settings, new
        {
            auditRetentionDays = 30,
            displayName = "   ",
            version = current.Version,
        });
        Assert.Null(current.DisplayName);
        Assert.Equal("Where the anvils come from.", current.Tagline);

        current = await _admin.PutJson<InstanceSettingsDto>(Settings, new
        {
            auditRetentionDays = 30,
            tagline = "",
            version = current.Version,
        });
        Assert.Null(current.Tagline);
        var served = await AnonymousConfigAsync();
        Assert.Null(served.DisplayName);
        Assert.Null(served.Tagline);
    }

    [SkippableTheory]
    [InlineData("Line\nbreak", null)]
    [InlineData("This name runs a good deal past forty characters", null)]
    [InlineData(null, "Two\nlines")]
    [InlineData("Acme Games", "Tab\there")]
    public async Task BrandingThatCannotBeDrawn_IsRefusedWhole(string? name, string? tagline)
    {
        postgres.SkipUnlessDockerAvailable();

        var current = await _admin.GetJson<InstanceSettingsDto>(Settings);
        var response = await _admin.PutAsJsonAsync(Settings, new
        {
            auditRetentionDays = current.AuditRetentionDays,
            displayName = name,
            tagline,
            version = current.Version,
        }, TestClients.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var served = await AnonymousConfigAsync();
        Assert.Null(served.DisplayName);
        Assert.Null(served.Tagline);
    }

    [SkippableFact]
    public async Task ATaglineLongerThanASentence_IsRefused()
    {
        postgres.SkipUnlessDockerAvailable();

        var current = await _admin.GetJson<InstanceSettingsDto>(Settings);
        var response = await _admin.PutAsJsonAsync(Settings, new
        {
            auditRetentionDays = current.AuditRetentionDays,
            tagline = new string('x', InstanceBranding.MaxTaglineLength + 1),
            version = current.Version,
        }, TestClients.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
