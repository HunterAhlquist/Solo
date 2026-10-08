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

namespace Solo.Api.Testing;

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

/// <summary>
/// The real host over a test database, signed in through
/// <see cref="TestAuthHandler"/>. Shared with the extensions' own test projects,
/// which load their packages into it — so it names no extension itself: a
/// caller passes the package directories it wants, and an extension that needs
/// more wiring subclasses it.
/// </summary>
/// <param name="packages">Unzipped package directories to load, in order.</param>
/// <param name="extensionsDirectory">A folder of package zips, as a deployment has.</param>
public class SoloApiFactory(
    string connectionString,
    IReadOnlyList<string>? packages = null,
    string? extensionsDirectory = null)
    : WebApplicationFactory<Program>
{
    public const string SetupUsername = "setup-admin";
    public const string SetupPassword = "first-run-setup-secret-for-tests";

    /// <summary>
    /// Where the build staged a package. Laid out as a real package directory
    /// (extension.json + server/) and discovered on disk, so the loader and its
    /// private AssemblyLoadContext are genuinely exercised — the test project
    /// deliberately references neither assembly.
    /// </summary>
    public static string PackageDirectory(string id) => Path.Combine(
        AppContext.BaseDirectory, "extension-fixtures", id);

    public static string FixtureDirectory => PackageDirectory("fixture");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Keep integration tests independent of local development secrets.
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();
        var slot = 0;
        foreach (var package in packages ?? [])
        {
            builder.UseSetting($"Extensions:DevelopmentDirectories:{slot++}", package);
        }

        if (extensionsDirectory is not null)
        {
            // The zip-scanning path a deployment actually uses, rather than the
            // unzipped development directories above.
            builder.UseSetting("Extensions:Directory", extensionsDirectory);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(new TestLogCollector()));
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Information");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Auth:Authority", "");
        builder.UseSetting("Auth:ClientId", "");
        builder.UseSetting("Auth:Provider", "generic");
        builder.UseSetting("Setup:Username", SetupUsername);
        builder.UseSetting("Setup:Password", SetupPassword);
        // The limiter middleware stays in the pipeline, but effectively
        // unlimited: every test client shares one "IP", so production limits
        // would trip on unrelated tests. RateLimitingTests tightens these.
        builder.UseSetting("RateLimiting:Api:Burst", "1000000");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "1000000");
        // Background work calls back into the API over HTTP (ExtensionBackground).
        // There is no socket under a test server, so point it at the server itself.
        builder.UseSetting("Extensions:ApiBaseUrl", "http://localhost/");
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient(Solo.Extensions.ExtensionBackground.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());

            // Auth:Authority is empty, so Program registered no "Bearer" scheme;
            // supply the header-driven test scheme under that name — with the
            // same forward rule production's JwtBearer carries, so sessions and
            // hub tickets are exercised exactly as deployed.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Bearer", options =>
                    options.ForwardDefaultSelector = Auth.BearerForwarding.Select);
        });
    }
}
