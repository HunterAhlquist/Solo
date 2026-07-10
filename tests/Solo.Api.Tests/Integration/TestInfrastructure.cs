using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Solo.Shared.Dtos;
using Testcontainers.PostgreSql;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// One Postgres container for the whole test run; each test class gets its own
/// database on it so state (e.g. the one-shot setup handoff) never leaks
/// between classes.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool DockerAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
            await _container.StartAsync();
            DockerAvailable = true;
        }
        catch (Exception)
        {
            DockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public void SkipUnlessDockerAvailable() =>
        Skip.IfNot(DockerAvailable, "Docker is not available; integration tests need it for Testcontainers.");

    /// <summary>
    /// A fresh database per call: xunit instantiates the test class (and thus
    /// runs InitializeAsync) once per test, so names must never repeat.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        var adminConnectionString = _container!.GetConnectionString();
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>
/// Replaces the OIDC bearer scheme in tests: identity claims come straight
/// from request headers, so provisioning/invites/authz run exactly as in
/// production.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Issuer = "https://test.example.com";
    public const string SubHeader = "X-Test-Sub";
    public const string EmailHeader = "X-Test-Email";
    public const string NameHeader = "X-Test-Name";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sub = Request.Headers[SubHeader].ToString();
        if (string.IsNullOrEmpty(sub))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("iss", Issuer), new("sub", sub) };
        var email = Request.Headers[EmailHeader].ToString();
        if (!string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim("email", email));
        }

        var name = Request.Headers[NameHeader].ToString();
        if (!string.IsNullOrEmpty(name))
        {
            claims.Add(new Claim("name", name));
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>Captures server-side errors so 500s show their cause in test output.</summary>
public sealed class TestLogCollector : ILoggerProvider
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Errors = new();

    public static string Drain()
    {
        var lines = new List<string>();
        while (Errors.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public ILogger CreateLogger(string categoryName) => new Collector(categoryName);

    public void Dispose()
    {
    }

    private sealed class Collector(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Error ||
            (logLevel >= LogLevel.Information &&
             category == "Microsoft.EntityFrameworkCore.Database.Command");

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Errors.Enqueue($"[{category}] {formatter(state, exception)}\n{exception}");
            }
        }
    }
}

public sealed class SoloApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string SetupUsername = "setup-admin";
    public const string SetupPassword = "first-run-secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddProvider(new TestLogCollector()));
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Information");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Auth:Authority", "");
        builder.UseSetting("Auth:ClientId", "");
        builder.UseSetting("Auth:Provider", "generic");
        builder.UseSetting("Setup:Username", SetupUsername);
        builder.UseSetting("Setup:Password", SetupPassword);
        builder.ConfigureServices(services =>
        {
            // Auth:Authority is empty, so Program registered no "Bearer" scheme;
            // supply the header-driven test scheme under that name — with the
            // same solo_ forward rule production's JwtBearer carries, so the
            // session scheme is exercised exactly as deployed.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Bearer", options =>
                    options.ForwardDefaultSelector = context =>
                        Auth.SessionTokens.IsSessionToken(Auth.SessionTokens.FromRequest(context.Request))
                            ? Auth.AuthConstants.SessionScheme
                            : null);
        });
    }
}

public static class TestClients
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>An HttpClient signed in as the given test persona.</summary>
    public static HttpClient As(this SoloApiFactory factory, string sub, string email, string? name = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, sub);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name ?? sub);
        return client;
    }

    public static HttpClient AsSetupAccount(this SoloApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{SoloApiFactory.SetupUsername}:{SoloApiFactory.SetupPassword}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Setup", token);
        return client;
    }

    /// <summary>Runs the first-run handoff, making the client's persona the super admin.</summary>
    public static async Task<UserDto> CompleteSetupAsync(HttpClient ssoClient)
    {
        var response = await ssoClient.PostAsJsonAsync("/api/setup/complete", new
        {
            setupUsername = SoloApiFactory.SetupUsername,
            setupPassword = SoloApiFactory.SetupPassword,
        }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>(Json))!;
    }

    public static async Task<T> GetJson<T>(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await EnsureSuccess(response, "GET", url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> PostJson<T>(this HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Json);
        await EnsureSuccess(response, "POST", url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> PutJson<T>(this HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body, Json);
        await EnsureSuccess(response, "PUT", url);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string method, string url)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var serverErrors = TestLogCollector.Drain();
            throw new InvalidOperationException(
                $"{method} {url} -> {(int)response.StatusCode} {response.StatusCode}: {body}\n{serverErrors}");
        }
    }
}
