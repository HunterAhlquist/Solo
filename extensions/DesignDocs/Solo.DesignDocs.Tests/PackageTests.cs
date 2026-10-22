using Solo.Shared.Dtos;

namespace Solo.DesignDocs.Tests;

[Collection("postgres")]
public sealed class PackageTests(PostgresFixture postgres)
{
    [SkippableFact]
    public async Task The_package_loads_through_the_real_extension_host()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("design_docs"),
            [SoloApiFactory.PackageDirectory("design-docs")]);
        using var admin = factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(admin);
        var inventory = await admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        var extension = Assert.Single(inventory.Extensions, e => e.ExtensionId == "design-docs");
        Assert.Equal("Loaded", extension.State);
        Assert.Equal("ext_design_docs", extension.SchemaName);
        var bundle = await admin.GetAsync("/api/extensions/design-docs/content/design-docs.js");
        Assert.True(bundle.IsSuccessStatusCode);
    }
}
