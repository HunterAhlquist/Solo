using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Solo.Client.Extensions;
using Solo.Shared.Kit;

namespace Solo.Client.Kit;

// An extension's whole page on the kit surface. The app asks for an address;
// the surface finds the page an extension has there and runs it as it runs a
// contribution, so a page built from the kit is one the app can draw as a
// screen of its own. A page that says it is the web's ([WebPage]), or that
// could not be given what it needs here, is answered with "web", and the app
// shows it from the web client instead.
//
// The router's own table cannot be asked — it is internal, and it answers for
// the address the browser is at, which here is the surface's. So the templates
// are matched again, as far as a component's templates go: literals,
// parameters with the router's constraints, an optional last parameter and a
// catch-all.

/// <summary>The page an extension has at an address, and what the address gives it.</summary>
/// <param name="Web">True when the page is the web client's to draw, here and in any app.</param>
public sealed record KitPage(string ExtensionId, Type Component, IDictionary<string, object> Parameters, bool Web);

public static class KitPages
{
    /// <summary>
    /// The page a loaded extension has at <paramref name="path"/>, relative to
    /// the instance, or null when none has one. Extensions the app draws itself
    /// are not looked in.
    /// </summary>
    public static KitPage? Resolve(string path, ExtensionCatalog catalog, IReadOnlySet<string> skip)
    {
        var address = path.Split('#', 2)[0];
        var parts = address.Split('?', 2);
        var segments = parts[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

        (KitPage Page, int Literals)? best = null;
        foreach (var (extensionId, component) in Components(catalog, skip))
        {
            foreach (var route in component.GetCustomAttributes<RouteAttribute>())
            {
                if (Match(route.Template, segments) is not { } match || best?.Literals >= match.Literals)
                {
                    continue;
                }

                // A query string belongs to the address the browser is at, which is never this page's.
                var web = component.IsDefined(typeof(WebPageAttribute)) || parts.Length > 1 || ReadsQuery(component);
                best = (new KitPage(extensionId, component, match.Parameters, web), match.Literals);
            }
        }

        return best?.Page;
    }

    /// <summary>Every routable component of the extensions the surface runs, with whose it is.</summary>
    public static IEnumerable<(string ExtensionId, Type Component)> Components(ExtensionCatalog catalog, IReadOnlySet<string> skip) =>
        catalog.Loaded
            .Where(e => !skip.Contains(e.Descriptor.Id))
            .SelectMany(e => e.Assembly.GetExportedTypes()
                .Where(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t) && t.IsDefined(typeof(RouteAttribute)))
                .Select(t => (e.Descriptor.Id, t)));

    private static bool ReadsQuery(Type component) =>
        component.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(p => p.IsDefined(typeof(SupplyParameterFromQueryAttribute)));

    /// <summary>
    /// Matches one template. More literal segments is the better match, as for
    /// the router: <c>things/new</c> wins over <c>things/{id}</c>.
    /// </summary>
    private static (IDictionary<string, object> Parameters, int Literals)? Match(string template, string[] segments)
    {
        var pattern = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var literals = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            var piece = pattern[i];
            if (!piece.StartsWith('{') || !piece.EndsWith('}'))
            {
                if (i >= segments.Length || !string.Equals(piece, segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                literals++;
                continue;
            }

            var inner = piece[1..^1];
            if (inner.StartsWith('*'))
            {
                // The rest of the address, however much there is, and only ever last.
                var rest = string.Join('/', segments.Skip(i));
                if (rest.Length > 0)
                {
                    parameters[inner.TrimStart('*')] = rest;
                }

                return (parameters, literals);
            }

            var optional = inner.EndsWith('?');
            var named = inner.TrimEnd('?').Split(':', 2);
            if (i >= segments.Length)
            {
                // Only what is left over may be missing, and only if all of it is optional.
                if (!optional)
                {
                    return null;
                }

                continue;
            }

            if (Convert(segments[i], named.Length > 1 ? named[1].TrimEnd('?') : null) is not { } value)
            {
                return null;
            }

            parameters[named[0]] = value;
        }

        return segments.Length > pattern.Length ? null : (parameters, literals);
    }

    /// <summary>A segment as the constraint's type, as the router hands it over, or null when it is not one.</summary>
    private static object? Convert(string segment, string? constraint)
    {
        const NumberStyles Whole = NumberStyles.Integer;
        var invariant = CultureInfo.InvariantCulture;
        return constraint?.ToLowerInvariant() switch
        {
            null or "" => segment,
            "guid" => Guid.TryParse(segment, out var guid) ? guid : null,
            "int" => int.TryParse(segment, Whole, invariant, out var number) ? number : null,
            "long" => long.TryParse(segment, Whole, invariant, out var number) ? number : null,
            "bool" => bool.TryParse(segment, out var flag) ? flag : null,
            "datetime" => DateTime.TryParse(segment, invariant, DateTimeStyles.None, out var date) ? date : null,
            "decimal" => decimal.TryParse(segment, NumberStyles.Number, invariant, out var number) ? number : null,
            "double" => double.TryParse(segment, NumberStyles.Float, invariant, out var number) ? number : null,
            "float" => float.TryParse(segment, NumberStyles.Float, invariant, out var number) ? number : null,
            // A constraint this doesn't know: the page is not matched, rather than handed the wrong thing.
            _ => null,
        };
    }
}
