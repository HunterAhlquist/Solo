using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Solo.Api.Updates;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Updating several extensions at once. The installs themselves are
/// <c>ExtensionUpdateInstallerTests</c>' to cover; what matters here is that a
/// batch reports each failure, and only restarts when something was installed.
/// </summary>
[Collection("postgres")]
public sealed class UpdatesApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(
            await postgres.CreateDatabaseAsync("updates"), [SoloApiFactory.FixtureDirectory]);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task UpdatingNothing_IsRejected()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _admin.PostAsJsonAsync(
            "/api/updates/extensions", new UpdateExtensionsRequest(), TestClients.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Neither id can be installed — the fixture is loaded from a directory
    /// rather than a package, and the other is not installed at all — so each
    /// is reported, and the instance stays up.
    /// </summary>
    [SkippableFact]
    public async Task ABatchWithNothingInstallable_ReportsEachFailure_AndDoesNotRestart()
    {
        postgres.SkipUnlessDockerAvailable();

        var result = await _admin.PostJson<ExtensionBulkUpdateResultDto>(
            "/api/updates/extensions", new UpdateExtensionsRequest { ExtensionIds = ["fixture", "absent", "ABSENT"] });

        Assert.False(result.Restarting);
        Assert.Empty(result.Installed);
        Assert.Equal(["fixture", "absent"], result.Failed.Select(f => f.ExtensionId));
        Assert.Contains("not installed", result.Failed[1].Reason);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/api/ready")).StatusCode);
    }

    [SkippableFact]
    public async Task OnlyTheSuperAdmin_CanUpdate()
    {
        postgres.SkipUnlessDockerAvailable();

        // A provisioned user, so the 403 is the super-admin policy and not the
        // fallback turning away an identity nobody invited.
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Updates", alias = "UPD" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "member@example.com", role = "Read" });
        var member = _factory.As("member", "member@example.com", "Member");
        await member.GetJson<UserDto>("/api/users/me");

        var response = await member.PostAsJsonAsync(
            "/api/updates/extensions", new UpdateExtensionsRequest { ExtensionIds = ["fixture"] }, TestClients.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A client whose update request timed out at a proxy polls this while the
    /// server is still downloading; "ready" then would reload it into the
    /// process that is about to restart.
    /// </summary>
    [SkippableFact]
    public async Task Ready_IsFalse_WhileUpdatesAreInstalling()
    {
        postgres.SkipUnlessDockerAvailable();

        var activity = _factory.Services.GetRequiredService<UpdateActivity>();
        var anonymous = _factory.CreateClient();

        activity.Begin();
        var during = await anonymous.GetAsync("/api/ready");
        activity.End();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/ready")).StatusCode);
    }
}
