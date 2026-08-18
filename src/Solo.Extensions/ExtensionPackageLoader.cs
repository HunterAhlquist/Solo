using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Solo.Extensibility;
using Solo.Shared.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Reads an extension package — a zip with <c>extension.json</c> at its root —
/// into a validated <see cref="ExtensionPackage"/>.
/// </summary>
/// <remarks>
/// <para>The checks here are deliberately the same shape as
/// <c>ThemePackageLoader</c>'s, and for the same reason: they are not a sandbox.
/// A server extension runs in-process with the connection string, so a hostile
/// package is game over regardless of what this validates (see
/// docs/extension-plan.md §1). What these do is stop a malformed, truncated or
/// absurdly large zip from taking the instance down, and turn "the extension
/// didn't load" into a message naming the reason.</para>
///
/// <para>The one thing they defend properly is the file system: an entry is
/// never written to disk and never escapes the package (zip slip), because
/// entries are read into memory by normalized relative path and nothing else.</para>
/// </remarks>
public static partial class ExtensionPackageLoader
{
    public const long MaxArchiveBytes = 64L * 1024 * 1024;
    public const long MaxTotalBytes = 192L * 1024 * 1024;
    public const long MaxEntryBytes = 64L * 1024 * 1024;
    public const int MaxEntries = 500;

    public const string ManifestName = "extension.json";
    public const string ServerFolder = "server/";
    public const string ClientFolder = "client/";
    public const string ContentFolder = "content/";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    public static ExtensionPackage LoadZip(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxArchiveBytes)
        {
            throw new ExtensionPackageException(
                $"Archive is {info.Length / 1024 / 1024} MB; the limit is {MaxArchiveBytes / 1024 / 1024} MB.");
        }

        using var archive = ZipFile.OpenRead(path);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var count = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0)
            {
                continue;
            }

            if (++count > MaxEntries)
            {
                throw new ExtensionPackageException($"Archive holds more than {MaxEntries} files.");
            }

            var relative = NormalizeEntryPath(entry.FullName)
                ?? throw new ExtensionPackageException($"Refusing entry with an unsafe path: '{entry.FullName}'.");

            if (entry.Length > MaxEntryBytes)
            {
                throw new ExtensionPackageException(
                    $"'{relative}' is {entry.Length / 1024 / 1024} MB; the per-file limit is " +
                    $"{MaxEntryBytes / 1024 / 1024} MB.");
            }

            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new ExtensionPackageException(
                    $"Archive expands to more than {MaxTotalBytes / 1024 / 1024} MB.");
            }

            files[relative] = ReadEntry(entry);
        }

        return Build(files, Path.GetFileName(path));
    }

    /// <summary>
    /// Loads a package laid out as a plain directory. Used by tests and by a
    /// development loop that would rather not re-zip on every build, so a
    /// directory goes through exactly the same validation a zip does.
    /// </summary>
    public static ExtensionPackage LoadDirectory(string root)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeEntryPath(Path.GetRelativePath(root, file));
            if (relative is null)
            {
                continue;
            }

            var length = new FileInfo(file).Length;
            total += length;
            if (length > MaxEntryBytes || total > MaxTotalBytes || files.Count >= MaxEntries)
            {
                throw new ExtensionPackageException($"Extension directory '{root}' exceeds the package size limits.");
            }

            files[relative] = File.ReadAllBytes(file);
        }

        return Build(files, new DirectoryInfo(root).Name);
    }

    private static ExtensionPackage Build(Dictionary<string, byte[]> files, string source)
    {
        if (!files.TryGetValue(ManifestName, out var manifestBytes))
        {
            throw new ExtensionPackageException($"No {ManifestName} at the package root.");
        }

        ExtensionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtensionManifest>(manifestBytes, ManifestJson)
                ?? throw new ExtensionPackageException($"{ManifestName} is empty.");
        }
        catch (JsonException e)
        {
            throw new ExtensionPackageException($"{ManifestName} is not valid JSON: {e.Message}");
        }

        Validate(manifest);

        var serverAssembly = Take(files, manifest.Server, "server");
        var clientAssembly = Take(files, manifest.Client, "client");

        return new ExtensionPackage
        {
            Manifest = manifest,
            Source = source,
            ServerAssembly = serverAssembly,
            ServerDependencies = Dependencies(files, ServerFolder, manifest.Server),
            ClientAssembly = clientAssembly,
            ClientDependencies = Dependencies(files, ClientFolder, manifest.Client),
            Content = files
                .Where(f => f.Key.StartsWith(ContentFolder, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    f => f.Key[ContentFolder.Length..],
                    f => new ExtensionContent(
                        f.Key[ContentFolder.Length..],
                        ContentTypes.GetValueOrDefault(Path.GetExtension(f.Key), "application/octet-stream"),
                        f.Value),
                    StringComparer.OrdinalIgnoreCase),
        };
    }

    private static void Validate(ExtensionManifest manifest)
    {
        if (!IdPattern().IsMatch(manifest.Id))
        {
            throw new ExtensionPackageException(
                $"\"id\" must be 1-64 characters of a-z, 0-9 and '-'; got '{manifest.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new ExtensionPackageException("\"version\" is required.");
        }

        if (manifest.Server is null && manifest.Client is null)
        {
            throw new ExtensionPackageException("A package must declare \"server\", \"client\", or both.");
        }

        // The schema name reaches a CREATE SCHEMA and a DROP SCHEMA, so it is
        // pinned to the extension's own id rather than trusted from the
        // manifest. An extension cannot name a schema it does not own, and a
        // purge can never be pointed at someone else's data.
        if (manifest.Schema is not null && manifest.Schema != ExpectedSchema(manifest.Id))
        {
            throw new ExtensionPackageException(
                $"\"schema\" must be '{ExpectedSchema(manifest.Id)}' for extension '{manifest.Id}'; " +
                $"got '{manifest.Schema}'.");
        }

        foreach (var hook in manifest.Hooks)
        {
            if (!Operations.All.Contains(hook))
            {
                throw new ExtensionPackageException($"\"hooks\" names an unknown operation '{hook}'.");
            }
        }

        foreach (var nav in manifest.Nav)
        {
            if (string.IsNullOrWhiteSpace(nav.Route) || !nav.Route.StartsWith('/'))
            {
                throw new ExtensionPackageException($"Nav route '{nav.Route}' must start with '/'.");
            }

            if (string.IsNullOrWhiteSpace(nav.Label))
            {
                throw new ExtensionPackageException("Every nav entry needs a \"label\".");
            }
        }

        RequireRange(manifest.SoloApiVersion, "soloApiVersion", manifest.Server is not null);
        RequireRange(manifest.SoloClientVersion, "soloClientVersion", manifest.Client is not null);
    }

    /// <summary>The only schema name an extension may own, derived from its id.</summary>
    public static string ExpectedSchema(string extensionId) =>
        "ext_" + extensionId.Replace('-', '_');

    private static void RequireRange(string? raw, string field, bool required)
    {
        if (raw is null)
        {
            if (required)
            {
                throw new ExtensionPackageException($"\"{field}\" is required for this package.");
            }

            return;
        }

        if (!VersionRange.TryParse(raw, out _, out var error))
        {
            throw new ExtensionPackageException($"\"{field}\" is invalid: {error}");
        }
    }

    private static byte[]? Take(Dictionary<string, byte[]> files, string? path, string kind)
    {
        if (path is null)
        {
            return null;
        }

        var normalized = NormalizeEntryPath(path);
        if (normalized is null || !files.TryGetValue(normalized, out var bytes))
        {
            throw new ExtensionPackageException($"Manifest names a {kind} assembly '{path}' that is not in the package.");
        }

        return bytes;
    }

    /// <summary>Every other .dll beside the named assembly — the package's private dependencies.</summary>
    private static IReadOnlyDictionary<string, byte[]> Dependencies(
        Dictionary<string, byte[]> files, string folder, string? mainPath)
    {
        var main = mainPath is null ? null : NormalizeEntryPath(mainPath);
        return files
            .Where(f => f.Key.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                        && f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(f.Key, main, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f.Key), f => f.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        // The declared length is the header's claim; the buffer bounds the read,
        // so a lying header truncates rather than expands.
        var buffer = new byte[entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string? NormalizeEntryPath(string raw)
    {
        var path = raw.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':'))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        path = string.Join('/', segments);
        return SafePathCharacters().IsMatch(path) ? path : null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafePathCharacters();
}
