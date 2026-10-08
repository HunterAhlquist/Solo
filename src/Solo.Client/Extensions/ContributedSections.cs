using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace Solo.Client.Extensions;

/// <summary>A component a loaded extension contributes to a section of a host page.</summary>
public sealed record ContributedSection(string Title, Type Component);

public static class ContributedSections
{
    /// <summary>
    /// Every component in a loaded extension that carries <typeparamref name="TAttribute"/>,
    /// in the order the attributes ask for and then by extension id, so the
    /// layout doesn't shift with load order.
    /// </summary>
    /// <remarks>Reflection over every exported type: callers cache the result per catalog change.</remarks>
    public static IReadOnlyList<ContributedSection> Find<TAttribute>(
        ExtensionCatalog catalog, Func<TAttribute, string> title, Func<TAttribute, int> order)
        where TAttribute : Attribute =>
        catalog.Loaded
            .SelectMany(e => e.Assembly.GetExportedTypes()
                .Where(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t))
                .Select(t => (e.Descriptor.Id, Type: t, Attribute: t.GetCustomAttribute<TAttribute>()))
                .Where(c => c.Attribute is not null))
            .OrderBy(c => order(c.Attribute!))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new ContributedSection(title(c.Attribute!), c.Type))
            .ToList();

    /// <summary>
    /// Components carrying <typeparamref name="TAttribute"/> where there is no
    /// header to draw — a card badge, an app bar button — in the same order.
    /// </summary>
    public static IReadOnlyList<Type> Components<TAttribute>(ExtensionCatalog catalog, Func<TAttribute, int> order)
        where TAttribute : Attribute =>
        Find(catalog, _ => string.Empty, order).Select(s => s.Component).ToList();

    /// <summary>
    /// Classes, not components, carrying <typeparamref name="TAttribute"/> and
    /// implementing <typeparamref name="TContract"/>: what the host instantiates
    /// and calls rather than renders, such as a task menu contributor. A class
    /// with the attribute but not the interface is skipped, not an error — the
    /// attribute alone does nothing.
    /// </summary>
    public static IReadOnlyList<Type> Implementations<TAttribute, TContract>(
        ExtensionCatalog catalog, Func<TAttribute, int> order)
        where TAttribute : Attribute =>
        catalog.Loaded
            .SelectMany(e => e.Assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(TContract).IsAssignableFrom(t))
                .Select(t => (e.Descriptor.Id, Type: t, Attribute: t.GetCustomAttribute<TAttribute>()))
                .Where(c => c.Attribute is not null))
            .OrderBy(c => order(c.Attribute!))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Type)
            .ToList();
}
