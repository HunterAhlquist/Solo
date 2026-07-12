using System.Net;
using System.Net.Http.Json;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

[Collection("postgres")]
public sealed class SetupFlowTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("setup_flow"));
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task AuthConfig_IsAnonymous()
    {
        postgres.SkipUnlessDockerAvailable();
        var config = await _factory.CreateClient().GetJson<AuthConfigDto>("/api/config/auth");
        Assert.False(config.SetupCompleted);
        Assert.Equal("generic", config.Provider);
    }

    [SkippableFact]
    public async Task Handoff_GrantsSuperAdmin_AndKillsTheStaticAccount()
    {
        postgres.SkipUnlessDockerAvailable();

        // Setup mode: the static account authenticates.
        var setupClient = _factory.AsSetupAccount();
        var verify = await setupClient.PostAsync("/api/setup/verify", null);
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);

        // Wizard: configure retention via the static account.
        var settings = await setupClient.PutJson<InstanceSettingsDto>(
            "/api/setup/settings", new { auditRetentionDays = 30 });
        Assert.Equal(30, settings.AuditRetentionDays);

        // Wrong static credentials are rejected on the SSO-authenticated handoff.
        var hunter = _factory.As("hunter", "hunter@example.com", "Hunter");
        var badHandoff = await hunter.PostAsJsonAsync("/api/setup/complete",
            new { setupUsername = "nope", setupPassword = "nope" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, badHandoff.StatusCode);

        // Real handoff: the SSO user becomes super admin.
        var admin = await TestClients.CompleteSetupAsync(hunter);
        Assert.True(admin.IsSuperAdmin);
        Assert.Equal("hunter@example.com", admin.Email);

        // The static account is permanently disabled once setup completes.
        var deadVerify = await setupClient.PostAsync("/api/setup/verify", null);
        Assert.Equal(HttpStatusCode.Unauthorized, deadVerify.StatusCode);

        // Once setup completes, an uninvited stranger is not even provisioned:
        // the handoff refuses before the conflict check can be reached.
        var intruder = _factory.As("intruder", "intruder@example.com");
        var strangerRepeat = await intruder.PostAsJsonAsync("/api/setup/complete", new
        {
            setupUsername = SoloApiFactory.SetupUsername,
            setupPassword = SoloApiFactory.SetupPassword,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, strangerRepeat.StatusCode);

        // A second handoff by a known user conflicts, even with valid credentials.
        var repeat = await hunter.PostAsJsonAsync("/api/setup/complete", new
        {
            setupUsername = SoloApiFactory.SetupUsername,
            setupPassword = SoloApiFactory.SetupPassword,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);

        var status = await hunter.GetJson<SetupStatusDto>("/api/setup/status");
        Assert.True(status.SetupCompleted);
    }
}
