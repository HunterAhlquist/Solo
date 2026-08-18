using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solo.Extensibility;
using Solo.Shared.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Discovers packages, checks their version ranges, loads their server
/// assemblies and lets each register its services.
/// </summary>
/// <remarks>
/// Runs before <c>builder.Build()</c> — MVC application parts and filters
/// cannot be added afterwards — which is why extension discovery is the first
/// thing in <c>Program.cs</c>. Nothing here throws: a package that fails is
/// recorded as a failure and skipped, and the instance starts without it. That
/// mirrors the theme catalog, and for the same reason: an operator's broken
/// drop-in must not be able to take the site down.
/// </remarks>
public static class ExtensionLoader
{
    public static ExtensionState Load(
        IServiceCollection services,
        ExtensionOptions options,
        string connectionString,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ExtensionLoader));
        var packages = new List<ExtensionPackage>();
        var failures = new List<ExtensionLoadFailure>();

        foreach (var (source, load) in Discover(options, logger))
        {
            ExtensionPackage package;
            try
            {
                package = load();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Extension package '{Source}' failed to load.", source);
                failures.Add(new ExtensionLoadFailure(source, e.Message));
                continue;
            }

            if (packages.Any(p => string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(new ExtensionLoadFailure(source, $"Another package already claims id '{package.Id}'."));
                continue;
            }

            if (!CheckServerRange(package, out var rangeError))
            {
                logger.LogError("Extension '{Id}' rejected: {Reason}", package.Id, rangeError);
                failures.Add(new ExtensionLoadFailure(source, rangeError!));
                continue;
            }

            if (!Activate(services, package, connectionString, loggerFactory, logger, out var activateError))
            {
                failures.Add(new ExtensionLoadFailure(source, activateError!));
                continue;
            }

            packages.Add(package);
        }

        logger.LogInformation(
            "Extensions loaded: {Count} package(s), {Failures} failure(s).", packages.Count, failures.Count);
        return new ExtensionState(packages, failures);
    }

    private static IEnumerable<(string Source, Func<ExtensionPackage> Load)> Discover(
        ExtensionOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.Directory))
        {
            if (Directory.Exists(options.Directory))
            {
                foreach (var file in SafeEnumerate(
                    () => Directory.EnumerateFiles(options.Directory!, "*.zip"), options.Directory!, logger))
                {
                    yield return (Path.GetFileName(file), () => ExtensionPackageLoader.LoadZip(file));
                }
            }
            else
            {
                // Not an error: a deployment without an extensions volume is normal.
                logger.LogInformation(
                    "No extensions directory at {Directory}; running without extensions.", options.Directory);
            }
        }

        foreach (var directory in options.DevelopmentDirectories.Where(Directory.Exists))
        {
            yield return (new DirectoryInfo(directory).Name, () => ExtensionPackageLoader.LoadDirectory(directory));
        }
    }

    private static IEnumerable<string> SafeEnumerate(
        Func<IEnumerable<string>> enumerate, string directory, ILogger logger)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Same failure mode the themes volume has: a non-root container and
            // a host-path mount the app cannot read.
            logger.LogError(e, "Cannot read the extensions directory {Directory}.", directory);
            return [];
        }
    }

    private static bool CheckServerRange(ExtensionPackage package, out string? error)
    {
        error = null;
        if (package.ServerAssembly is null)
        {
            return true;
        }

        if (!VersionRange.TryParse(package.Manifest.SoloApiVersion, out var range, out var parseError))
        {
            error = $"\"soloApiVersion\" is invalid: {parseError}";
            return false;
        }

        if (!range.Includes(ContractVersion.Parsed))
        {
            error = $"Requires Solo extension API {range}; this instance provides {ContractVersion.Current}.";
            return false;
        }

        return true;
    }

    /// <summary>Loads the server assembly, finds the entry point and lets it register services.</summary>
    private static bool Activate(
        IServiceCollection services,
        ExtensionPackage package,
        string connectionString,
        ILoggerFactory loggerFactory,
        ILogger logger,
        out string? error)
    {
        error = null;
        if (package.ServerAssembly is null)
        {
            return true;
        }

        try
        {
            var context = new ExtensionLoadContext(package.Id, package.ServerDependencies);
            var assembly = context.LoadFromStream(new MemoryStream(package.ServerAssembly, writable: false));
            package.LoadedAssembly = assembly;

            var entryType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(ISoloExtension).IsAssignableFrom(t) && t is
                    { IsAbstract: false, IsInterface: false });
            if (entryType is null)
            {
                error = $"'{package.Manifest.Server}' contains no {nameof(ISoloExtension)} implementation.";
                return false;
            }

            if (Activator.CreateInstance(entryType) is not ISoloExtension extension)
            {
                error = $"'{entryType.FullName}' could not be constructed; it needs a public parameterless constructor.";
                return false;
            }

            extension.ConfigureServices(services, new ExtensionContext(package, connectionString, loggerFactory));
            return true;
        }
        catch (ReflectionTypeLoadException e)
        {
            // Almost always a private copy of a shared contract assembly, or a
            // dependency the package forgot to ship.
            var detail = string.Join("; ", e.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message).Take(3));
            error = $"Loading '{package.Manifest.Server}' failed: {detail}";
            logger.LogError(e, "Extension '{Id}' failed to load its types.", package.Id);
            return false;
        }
        catch (Exception e)
        {
            error = $"Loading '{package.Manifest.Server}' failed: {e.Message}";
            logger.LogError(e, "Extension '{Id}' failed to activate.", package.Id);
            return false;
        }
    }

    private sealed class ExtensionContext(
        ExtensionPackage package, string connectionString, ILoggerFactory loggerFactory) : IExtensionContext
    {
        public ExtensionManifest Manifest => package.Manifest;

        public string Id => package.Id;

        public string? Schema => package.Manifest.Schema;

        public string ConnectionString => connectionString;

        public ILoggerFactory LoggerFactory => loggerFactory;
    }
}
