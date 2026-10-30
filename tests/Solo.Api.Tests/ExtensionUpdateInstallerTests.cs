using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solo.Api.Updates;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared.Extensibility;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// The installer downloads code and arranges for the instance to run it at the
/// next boot, so every refusal here is load-bearing. These drive the whole
/// thing against a stubbed transport and a real extensions directory, because
/// the failures that matter — a tampered download accepted, the old package
/// left beside the new one, a feed choosing a path — are all about what ends up
/// on disk.
/// </summary>
public sealed class ExtensionUpdateInstallerTests : IDisposable
{
    private const string Id = "roadmap";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "solo-update-tests", Guid.NewGuid().ToString("N"));

    private readonly string _extensionsDir;

    public ExtensionUpdateInstallerTests()
    {
        _extensionsDir = Path.Combine(_root, "extensions");
        Directory.CreateDirectory(_extensionsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallsAVerifiedUpdateAndKeepsThePreviousPackage()
    {
        var installed = Install("1.0.0");
        var update = Package("1.1.0");

        var outcome = await RunAsync(installed, update, Feed("1.1.0", update, $"solo-{Id}-1.1.0.zip"));

        Assert.Null(outcome.Error);
        Assert.Equal("1.1.0", outcome.Version);
        Assert.True(File.Exists(Path.Combine(_extensionsDir, $"solo-{Id}-1.1.0.zip")));

        // The old zip has to be gone from the scan: two packages claiming one id
        // make the loader reject the second, which presents as the extension
        // vanishing after an update.
        Assert.False(File.Exists(installed));
        Assert.Single(Directory.GetFiles(_extensionsDir, "*.zip"));

        // One rollback slot, named for the extension rather than the package it
        // holds, carrying the bytes just replaced.
        var rollback = Path.Combine(_extensionsDir, $"solo-{Id}.rollback.zip.bak");
        Assert.True(File.Exists(rollback));
        Assert.Single(Directory.GetFiles(_extensionsDir, "*.bak"));
    }

    /// <summary>
    /// The single most important refusal: the feed says where the bytes come
    /// from, so nothing may take its word for what they are.
    /// </summary>
    [Fact]
    public async Task RefusesADownloadThatDoesNotMatchTheDigest()
    {
        var installed = Install("1.0.0");
        var genuine = Package("1.1.0");

        // Tampered without changing the length, so the size check cannot be what
        // catches it and the digest is genuinely the thing under test.
        var tampered = genuine.ToArray();
        tampered[^1] ^= 0xFF;

        var entry = Feed("1.1.0", genuine, $"solo-{Id}-1.1.0.zip");

        var outcome = await RunAsync(installed, tampered, entry);

        Assert.Contains("does not match the digest", outcome.Error);
        Assert.True(File.Exists(installed));
        Assert.Single(Directory.GetFiles(_extensionsDir, "*.zip"));
        Assert.Empty(Directory.GetFiles(_extensionsDir, "*.tmp"));
    }

    [Fact]
    public async Task RefusesAPackageDeclaringADifferentId()
    {
        var installed = Install("1.0.0");
        var impostor = Package("1.1.0", id: "mcp");

        var outcome = await RunAsync(installed, impostor, Feed("1.1.0", impostor, "solo-mcp-1.1.0.zip"));

        Assert.Contains("declares id 'mcp'", outcome.Error);
        Assert.True(File.Exists(installed));
    }

    /// <summary>
    /// A digest proves the bytes are the ones the feed named; it says nothing
    /// about what they contain. A feed offering 1.1.0 and shipping 1.2.0 would
    /// leave the admin page claiming a version that is not installed.
    /// </summary>
    [Fact]
    public async Task RefusesAPackageWhoseVersionDisagreesWithTheFeed()
    {
        var installed = Install("1.0.0");
        var other = Package("1.2.0");

        var outcome = await RunAsync(installed, other, Feed("1.1.0", other, $"solo-{Id}-1.1.0.zip"));

        Assert.Contains("is version 1.2.0", outcome.Error);
        Assert.True(File.Exists(installed));
    }

    [Fact]
    public async Task RefusesAPackageThisServerCouldNotLoad()
    {
        var installed = Install("1.0.0");
        var future = Package("1.1.0", apiRange: ">=99.0 <100.0");

        var outcome = await RunAsync(installed, future, Feed("1.1.0", future, $"solo-{Id}-1.1.0.zip"));

        Assert.Contains("Update Solo first", outcome.Error);
        Assert.True(File.Exists(installed));
    }

    /// <summary>
    /// The quiet half of the compatibility check, and the one that was missing:
    /// a client range this build does not satisfy installs fine and loads fine
    /// on the server, and then the extension's pages are skipped in the browser.
    /// The extension looks alive and broken at the same time.
    /// </summary>
    [Fact]
    public async Task RefusesAPackageWhoseClientHalfThisBuildCouldNotLoad()
    {
        var installed = Install("1.0.0");
        var future = Package("1.1.0", clientRange: ">=99.0 <100.0");

        var outcome = await RunAsync(installed, future, Feed("1.1.0", future, $"solo-{Id}-1.1.0.zip"));

        Assert.Contains("Solo client in the range", outcome.Error);
        Assert.Contains("pages would not load", outcome.Error);
        Assert.True(File.Exists(installed));
    }

    /// <summary>
    /// One rollback per extension. The backup used to be named after the package
    /// it held, so each update minted a new one and none was ever replaced —
    /// leaving a multi-megabyte copy of every version ever installed, and
    /// nothing to say which was the rollback.
    /// </summary>
    [Fact]
    public async Task KeepsExactlyOneRollbackAcrossSuccessiveUpdates()
    {
        var first = Install("1.0.0");
        var second = Package("1.1.0");
        Assert.Null((await RunAsync(first, second, Feed("1.1.0", second, $"solo-{Id}-1.1.0.zip"))).Error);

        var third = Package("1.2.0");
        var outcome = await RunAsync(
            Path.Combine(_extensionsDir, $"solo-{Id}-1.1.0.zip"),
            third,
            Feed("1.2.0", third, $"solo-{Id}-1.2.0.zip"));

        Assert.Null(outcome.Error);

        // One package, one rollback, whatever the update history.
        Assert.Equal(
            new[] { $"solo-{Id}-1.2.0.zip" },
            Directory.GetFiles(_extensionsDir, "*.zip").Select(Path.GetFileName).ToArray());
        Assert.Equal(
            new[] { $"solo-{Id}.rollback.zip.bak" },
            Directory.GetFiles(_extensionsDir, "*.bak").Select(Path.GetFileName).ToArray());

        // And it holds the package immediately replaced, not the oldest one —
        // the slot is overwritten rather than added to.
        var rollback = Path.Combine(_extensionsDir, $"solo-{Id}.rollback.zip.bak");
        Assert.Equal(second, await File.ReadAllBytesAsync(rollback, CancellationToken.None));
    }

    /// <summary>
    /// An update touches two files and no others. Pinned because an earlier
    /// revision globbed <c>solo-&lt;id&gt;-*.zip.bak</c> to tidy up its own
    /// backups: extension ids may contain hyphens, so updating <c>roadmap</c>
    /// matched <c>solo-roadmap-pro-*.zip.bak</c> and deleted a different
    /// extension's only recovery package — and that pattern is also exactly
    /// what an operator keeping a copy of a package would name it.
    /// </summary>
    [Fact]
    public async Task DeletesNothingBesidesThePackageItReplaces()
    {
        var installed = Install("1.0.0");

        // A sibling extension whose id starts with this one's, plus its
        // rollback, plus a copy an operator made by hand.
        var bystanders = new[]
        {
            Path.Combine(_extensionsDir, $"solo-{Id}-pro-1.4.0.zip.bak"),
            Path.Combine(_extensionsDir, $"solo-{Id}-pro.rollback.zip.bak"),
            Path.Combine(_extensionsDir, $"solo-{Id}-0.9.0.zip.bak"),
            Path.Combine(_extensionsDir, "keep-me.zip.bak"),
        };
        foreach (var bystander in bystanders)
        {
            await File.WriteAllTextAsync(bystander, bystander, CancellationToken.None);
        }

        var update = Package("1.1.0");
        var outcome = await RunAsync(installed, update, Feed("1.1.0", update, $"solo-{Id}-1.1.0.zip"));

        Assert.Null(outcome.Error);
        foreach (var bystander in bystanders)
        {
            Assert.True(File.Exists(bystander), $"{Path.GetFileName(bystander)} must survive an update");
            Assert.Equal(bystander, await File.ReadAllTextAsync(bystander, CancellationToken.None));
        }
    }

    /// <summary>The rollback must not be a package the loader would pick up.</summary>
    [Fact]
    public async Task TheRollbackIsNotScannedAsAPackage()
    {
        var installed = Install("1.0.0");
        var update = Package("1.1.0");

        Assert.Null((await RunAsync(installed, update, Feed("1.1.0", update, $"solo-{Id}-1.1.0.zip"))).Error);

        // *.zip is what ExtensionLoader.Discover enumerates.
        Assert.Single(Directory.GetFiles(_extensionsDir, "*.zip"));
    }

    [Fact]
    public async Task RefusesAnInsecureUrl()
    {
        var installed = Install("1.0.0");
        var update = Package("1.1.0");
        var entry = Feed("1.1.0", update, $"solo-{Id}-1.1.0.zip");
        entry.Url = "http://example.invalid/package.zip";

        var outcome = await RunAsync(installed, update, entry);

        Assert.Contains("not an https URL", outcome.Error);
    }

    [Fact]
    public async Task RefusesAPackageOverTheSizeLimit()
    {
        var installed = Install("1.0.0");
        var update = Package("1.1.0");
        var entry = Feed("1.1.0", update, $"solo-{Id}-1.1.0.zip");

        var outcome = await RunAsync(
            installed, update, entry, options => options.MaxPackageBytes = 16);

        Assert.Contains("limit", outcome.Error);
        Assert.True(File.Exists(installed));
    }

    /// <summary>
    /// An asset name is remote input and reaches a file path, so a feed must not
    /// be able to choose where the write lands. The installer falls back to a
    /// name it builds itself.
    /// </summary>
    [Theory]
    [InlineData("../../escape.zip")]
    [InlineData("/etc/solo.zip")]
    [InlineData("nested/dir/pkg.zip")]
    [InlineData("package.txt")]
    [InlineData("")]
    public async Task RefusesToTakeAPathFromTheFeedsAssetName(string asset)
    {
        var installed = Install("1.0.0");
        var update = Package("1.1.0");

        var outcome = await RunAsync(installed, update, Feed("1.1.0", update, asset));

        Assert.Null(outcome.Error);

        // Written under a name the installer chose, and nothing landed outside
        // the extensions directory.
        var written = Directory.GetFiles(_extensionsDir, "*.zip").Single();
        Assert.Equal($"solo-{Id}-1.1.0.zip", Path.GetFileName(written));
        Assert.Empty(Directory.GetFiles(_root, "*.zip", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task RefusesAnExtensionLoadedFromADevelopmentDirectory()
    {
        var update = Package("1.1.0");
        var outcome = await RunAsync(
            installedPath: null,
            update,
            Feed("1.1.0", update, $"solo-{Id}-1.1.0.zip"),
            source: "Solo.Roadmap");

        Assert.Contains("not a package in the extensions directory", outcome.Error);
    }

    [Fact]
    public async Task RefusesWhenNothingNewerIsOffered()
    {
        var installed = Install("1.0.0");
        var same = Package("1.0.0");

        var outcome = await RunAsync(installed, same, Feed("1.0.0", same, $"solo-{Id}-1.0.0.zip"));

        Assert.Contains("already current", outcome.Error);
    }

    // -- harness ------------------------------------------------------------

    private async Task<InstallOutcome> RunAsync(
        string? installedPath,
        byte[] served,
        ReleaseFeedExtension entry,
        Action<UpdateOptions>? configure = null,
        string? source = null)
    {
        var manifest = new ExtensionManifest
        {
            Id = Id,
            Name = "Roadmap",
            Version = installedPath is null ? "1.0.0" : VersionOf(installedPath),
            SoloApiVersion = ">=1.0 <2.0",
            Server = "server/Thing.dll",
        };

        var package = new ExtensionPackage
        {
            Manifest = manifest,
            Source = source ?? Path.GetFileName(installedPath!),
            ServerAssembly = [1],
        };

        var state = new ExtensionState([package], []);

        var options = new UpdateOptions
        {
            // Not reached: the host feed is stubbed to 404 so resolution falls
            // through to the package's own feed, which is the third-party path.
            ManifestUrl = "https://host.invalid/release-manifest.json",
        };
        configure?.Invoke(options);
        manifest.Updates = "https://feed.invalid/release-manifest.json";

        var feed = new ReleaseFeed { Schema = 1, Extensions = [entry] };
        var factory = new StubFactory(feed, served, entry.Url);

        var client = new ReleaseFeedClient(
            factory,
            Options.Create(options),
            TimeProvider.System,
            NullLogger<ReleaseFeedClient>.Instance);

        var installer = new ExtensionUpdateInstaller(
            client,
            state,
            factory,
            Options.Create(options),
            Options.Create(new ExtensionOptions { Directory = _extensionsDir }),
            NullLogger<ExtensionUpdateInstaller>.Instance);

        return await installer.InstallAsync(Id, CancellationToken.None);
    }

    private static string VersionOf(string path) =>
        Path.GetFileNameWithoutExtension(path).Split('-').Last();

    /// <summary>Writes a package into the extensions directory, as an install would.</summary>
    private string Install(string version)
    {
        var path = Path.Combine(_extensionsDir, $"solo-{Id}-{version}.zip");
        File.WriteAllBytes(path, Package(version));
        return path;
    }

    private static byte[] Package(
        string version,
        string id = Id,
        string apiRange = ">=1.0 <2.0",
        string? extra = null,
        string clientRange = ">=1.0 <2.0")
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = $$"""
            {
              "id": "{{id}}",
              "name": "Roadmap",
              "version": "{{version}}",
              "soloApiVersion": "{{apiRange}}",
              "soloClientVersion": "{{clientRange}}",
              "server": "server/Thing.dll",
              "client": "client/Thing.Client.dll",
              "schema": "ext_{{id}}"
            }
            """;
            Write(archive, "extension.json", Encoding.UTF8.GetBytes(manifest));
            Write(archive, "server/Thing.dll", "MZ-ish"u8.ToArray());
            Write(archive, "client/Thing.Client.dll", "MZ-ish"u8.ToArray());
            if (extra is not null)
            {
                Write(archive, "content/extra.txt", Encoding.UTF8.GetBytes(extra));
            }
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] bytes)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(bytes);
    }

    /// <summary>A feed entry describing <paramref name="bytes"/> truthfully.</summary>
    private static ReleaseFeedExtension Feed(string version, byte[] bytes, string asset) => new()
    {
        Id = Id,
        Version = version,
        Asset = asset,
        Url = "https://feed.invalid/" + (string.IsNullOrEmpty(asset) ? "pkg" : asset),
        Size = bytes.LongLength,
        Sha512 = Convert.ToBase64String(SHA512.HashData(bytes)),
        SoloApiVersion = ">=1.0 <2.0",
    };

    /// <summary>
    /// Serves the feed as JSON and the package URL as bytes; everything else
    /// 404s, which is how the host manifest is made to fall through to the
    /// package's own.
    /// </summary>
    private sealed class StubFactory(ReleaseFeed feed, byte[] package, string? packageUrl)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(feed, package, packageUrl)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class StubHandler(ReleaseFeed feed, byte[] package, string? packageUrl)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            // Compared as Uris: a feed asset name like "../../escape.zip" is
            // normalised away by Uri before the request is made, so matching the
            // raw string from the feed would never hit.
            if (packageUrl is not null &&
                Uri.TryCreate(packageUrl, UriKind.Absolute, out var expected) &&
                url == expected.ToString())
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(package),
                });
            }

            if (url.Contains("feed.invalid") && url.EndsWith("release-manifest.json"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(feed),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
