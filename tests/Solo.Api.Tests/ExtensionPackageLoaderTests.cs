using System.IO.Compression;
using System.Text;
using Solo.Extensions;
using Solo.Shared.Extensibility;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// Manifest validation and the zip hardening. These are not a sandbox — a
/// server extension runs in-process with the connection string — they are what
/// stops a malformed drop-in taking the instance down and what turns a failure
/// into a sentence naming the reason.
/// </summary>
public sealed class ExtensionPackageLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "solo-ext-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void LoadsAValidPackage()
    {
        var path = Zip(("extension.json", Manifest()), ("server/Thing.dll", "MZ-ish"u8.ToArray()));

        var package = ExtensionPackageLoader.LoadZip(path);

        Assert.Equal("roadmap", package.Id);
        Assert.Equal("ext_roadmap", package.Manifest.Schema);
        Assert.NotNull(package.ServerAssembly);
    }

    [Fact]
    public void RejectsAPackageWithNoManifest()
    {
        var path = Zip(("server/Thing.dll", "MZ"u8.ToArray()));

        var error = Assert.Throws<ExtensionPackageException>(() => ExtensionPackageLoader.LoadZip(path));
        Assert.Contains("extension.json", error.Message);
    }

    [Theory]
    [InlineData("Roadmap")]
    [InlineData("road map")]
    [InlineData("road/map")]
    [InlineData("")]
    public void RejectsAnInvalidId(string id)
    {
        var path = Zip(("extension.json", Manifest(id: id)), ("server/Thing.dll", "MZ"u8.ToArray()));

        Assert.Throws<ExtensionPackageException>(() => ExtensionPackageLoader.LoadZip(path));
    }

    /// <summary>
    /// The schema name reaches a CREATE SCHEMA and a DROP SCHEMA, so it is
    /// pinned to the extension's own id rather than trusted from the manifest —
    /// an extension cannot name a schema it does not own, and a purge can never
    /// be pointed at core's data.
    /// </summary>
    [Theory]
    [InlineData("public")]
    [InlineData("ext_something_else")]
    public void RejectsASchemaThatIsNotDerivedFromTheId(string schema)
    {
        var path = Zip(("extension.json", Manifest(schema: schema)), ("server/Thing.dll", "MZ"u8.ToArray()));

        var error = Assert.Throws<ExtensionPackageException>(() => ExtensionPackageLoader.LoadZip(path));
        Assert.Contains("ext_roadmap", error.Message);
    }

    [Fact]
    public void RejectsAnUnknownHookOperation()
    {
        var path = Zip(
            ("extension.json", Manifest(hooks: "\"task.explode\"")),
            ("server/Thing.dll", "MZ"u8.ToArray()));

        var error = Assert.Throws<ExtensionPackageException>(() => ExtensionPackageLoader.LoadZip(path));
        Assert.Contains("task.explode", error.Message);
    }

    [Fact]
    public void RejectsAnEntryThatEscapesThePackage()
    {
        var path = Path.Combine(_root, "evil.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, "extension.json", Manifest());
            Write(archive, "../../../etc/passwd", "pwned"u8.ToArray());
        }

        var error = Assert.Throws<ExtensionPackageException>(() => ExtensionPackageLoader.LoadZip(path));
        Assert.Contains("unsafe path", error.Message);
    }

    [Fact]
    public void RejectsAManifestNamingAnAssemblyThatIsNotInThePackage()
    {
        var path = Zip(("extension.json", Manifest()));

        var error = Assert.Throws<ExtensionPackageException>(() => ExtensionPackageLoader.LoadZip(path));
        Assert.Contains("not in the package", error.Message);
    }

    [Fact]
    public void ExpectedSchema_TurnsHyphensIntoUnderscores()
    {
        Assert.Equal("ext_my_thing", ExtensionPackageLoader.ExpectedSchema("my-thing"));
    }

    private static byte[] Manifest(
        string id = "roadmap", string? schema = "ext_roadmap", string hooks = "") =>
        Encoding.UTF8.GetBytes($$"""
        {
          "id": "{{id}}",
          "name": "Roadmap",
          "version": "1.0.0",
          "soloApiVersion": ">=1.0 <2.0",
          "server": "server/Thing.dll",
          "schema": "{{schema}}",
          "hooks": [{{hooks}}]
        }
        """);

    private string Zip(params (string Path, byte[] Bytes)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, bytes) in entries)
        {
            Write(archive, entryPath, bytes);
        }

        return path;
    }

    private static void Write(ZipArchive archive, string path, byte[] bytes)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(bytes);
    }
}

public sealed class VersionRangeTests
{
    [Theory]
    [InlineData(">=1.0 <2.0", "1.0.0", true)]
    [InlineData(">=1.0 <2.0", "1.9.9", true)]
    [InlineData(">=1.0 <2.0", "2.0.0", false)]
    [InlineData(">=1.0 <2.0", "0.9.0", false)]
    [InlineData(">=1", "3.0.0", true)]
    [InlineData("<2", "1.0.0", true)]
    public void EvaluatesRanges(string range, string version, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed, out _));
        Assert.Equal(expected, parsed.Includes(Version.Parse(version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("~>1.0")]
    [InlineData(">=banana")]
    public void RejectsWhatItCannotUnderstand(string range)
    {
        // Never silently permissive: unparseable input is a load failure with
        // the reason, not an empty range that matches everything.
        Assert.False(VersionRange.TryParse(range, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

/// <summary>
/// The contract package cannot reference Solo.Api, so it restates the policy
/// names. A drift between the two is invisible at compile time and shows up as
/// a 500 on every extension endpoint that authorizes — which is exactly how it
/// was found the first time.
/// </summary>
public sealed class ExtensionPolicyNamesTests
{
    [Fact]
    public void ContractPolicyNames_MatchCore()
    {
        Assert.Equal(Solo.Api.Auth.AuthConstants.ProjectReadPolicy, Solo.Extensibility.SoloPolicies.ProjectRead);
        Assert.Equal(Solo.Api.Auth.AuthConstants.ProjectWritePolicy, Solo.Extensibility.SoloPolicies.ProjectWrite);
        Assert.Equal(Solo.Api.Auth.AuthConstants.ProjectOwnerPolicy, Solo.Extensibility.SoloPolicies.ProjectOwner);
        Assert.Equal(Solo.Api.Auth.AuthConstants.SuperAdminPolicy, Solo.Extensibility.SoloPolicies.SuperAdmin);
    }
}
