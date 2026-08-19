using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solo.Api.Theming;
using Solo.Extensions;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Settings a deployment must not have to supply.
/// </summary>
/// <remarks>
/// <para>The image creates <c>/data/themes</c> and <c>/data/extensions</c>, the
/// deployment template mounts volumes there, and the docs tell operators to drop
/// packages in. All of that is a promise that an instance scans those paths
/// without being told to, and these pin it.</para>
///
/// <para>Written after the promise turned out to be half-kept: themes had a
/// default in appsettings.json and extensions did not, so a compose file that
/// set neither loaded themes and silently ignored a package sitting in the
/// extensions volume.</para>
/// </remarks>
[Collection("postgres")]
public sealed class DeploymentDefaultsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("defaults"));
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public void The_themes_directory_defaults_to_the_path_the_image_creates()
    {
        postgres.SkipUnlessDockerAvailable();

        Assert.Equal("/data/themes", Options<ThemeOptions>().Directory);
    }

    [SkippableFact]
    public void The_extensions_directory_defaults_to_the_path_the_image_creates()
    {
        postgres.SkipUnlessDockerAvailable();

        Assert.Equal("/data/extensions", Options<ExtensionOptions>().Directory);
    }

    /// <summary>
    /// Resolved through the running host, not by reading appsettings.json — the
    /// binding is half of what makes a default real.
    /// </summary>
    private TOptions Options<TOptions>() where TOptions : class =>
        _factory.Services.GetRequiredService<IOptions<TOptions>>().Value;
}
