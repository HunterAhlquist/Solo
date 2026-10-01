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
}
