using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The compose template's placeholder is public along with the repo, so an
    /// instance deployed without changing it would belong to whoever typed the
    /// template's pair into its wizard first. A short password goes the same
    /// way: until the handoff, only a per-IP rate limit stands in front of it.
    /// </summary>
    [SkippableTheory]
    [InlineData("CHANGE_ME_SETUP_PASSWORD")]
    [InlineData("change_me_but_lowercase_and_long")]
    [InlineData("nineteen-characters")]
    public async Task AWeakSetupPassword_DisablesTheSetupAccount(string password)
    {
        postgres.SkipUnlessDockerAvailable();

        var log = new ErrorLog();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Setup:Password", password);
            builder.ConfigureLogging(logging => logging.AddProvider(log));
        });

        // The configured pair, typed exactly, still doesn't authenticate.
        using var setupClient = factory.CreateClient();
        setupClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Setup",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{SoloApiFactory.SetupUsername}:{password}")));
        var verify = await setupClient.PostAsync("/api/setup/verify", null);
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);

        // The wizard is told why, or the operator retypes a password that is
        // right and concludes the app is broken.
        using var problem = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.Contains("Setup__Password", problem.RootElement.GetProperty("detail").GetString());

        // The handoff checks the pair itself, so it has to refuse it too.
        using var hunter = factory.As("hunter", "hunter@example.com", "Hunter");
        var handoff = await hunter.PostAsJsonAsync("/api/setup/complete",
            new { setupUsername = SoloApiFactory.SetupUsername, setupPassword = password }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, handoff.StatusCode);

        // And the startup log names the setting, where the operator looks first.
        Assert.Contains(log.Errors, line => line.Contains("Setup__Password"));
    }

    /// <summary>Collects error-level log lines from one host.</summary>
    private sealed class ErrorLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Errors { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Errors);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    errors.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
