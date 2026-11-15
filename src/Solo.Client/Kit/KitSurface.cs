using System.Reflection;
using Microsoft.AspNetCore.Components;
using Solo.Client.Extensions;
using Solo.Shared.Extensibility;

namespace Solo.Client.Kit;

// The kit surface: what an app that hosts the client uses to draw extensions'
// contributions with controls of its own. The app keeps one page of the
// client open where nobody sees it (KitSurfacePage) and asks it to mount a
// place — this project's card, that task's detail. The page runs the
// contributions for that place exactly as a core page would, with the same
// parameters, except that a kit component there is not drawn: it becomes a
// node saying what it is, and a script (js/kit-surface.js) hands the app the
// nodes as a tree. A press in the app comes back as a click on the node it was
// made from, so the extension's own handler answers it.
//
// Only kit components cross over. Anything else a contribution renders stays
// in the page nobody sees.
//
// An extension's own page can be mounted the same way (KitPages), and there
// what does not cross over matters: a page is either drawn whole by the app or
// shown whole from the web, so the app is told which.

/// <summary>
/// Cascaded over everything the surface page mounts. A kit component drawn
/// under one is written out as a node rather than as MudBlazor (KitView).
/// </summary>
public sealed class KitSurface;

/// <summary>A place the app asked the surface to run contributions for.</summary>
/// <param name="Id">The app's own name for this mount, which every tree it is sent carries.</param>
/// <param name="Seam">One of <see cref="KitSeams"/>.</param>
/// <param name="ExtensionId">For a place that is one extension's alone: its settings panel.</param>
/// <param name="Path">For a whole page: its address, relative to the instance.</param>
public sealed record KitMount(
    string Id, string Seam, Guid? ProjectId, Guid? BoardId, Guid? TaskId, string? ExtensionId = null, string? Path = null)
{
    /// <summary>Moved by the app when what the mount shows is stale and should be read again.</summary>
    public int Revision { get; init; }
}

/// <summary>One extension's component for a seam.</summary>
public sealed record KitContribution(string ExtensionId, string Title, int Order, Type Component);

/// <summary>The places the surface can mount, by the names the app uses for them.</summary>
public static class KitSeams
{
    public const string ProjectCard = "projectCard";
    public const string ProjectHome = "projectHome";
    public const string ProjectNav = "projectNav";
    public const string Dashboard = "dashboard";
    public const string TaskDetail = "taskDetail";
    public const string TaskCard = "taskCard";

    /// <summary>The cards of Your Tasks: the same contributions as a board's cards, over the person's own work.</summary>
    public const string AssignedCards = "assignedCards";
    public const string BoardColumn = "boardColumn";
    public const string ProjectSettings = "projectSettings";
    public const string BoardSettings = "boardSettings";
    public const string ExtensionSettings = "extensionSettings";
    public const string AppBar = "appBar";

    /// <summary>What extensions add to a task's menu. Classes rather than components: see <see cref="MenuContributors"/>.</summary>
    public const string TaskMenu = "taskMenu";

    /// <summary>An extension's whole page, for an app that draws pages built from the kit itself: see <see cref="KitPages"/>.</summary>
    public const string Page = "page";

    public static readonly IReadOnlyList<string> All =
    [
        ProjectCard, ProjectHome, ProjectNav, Dashboard, TaskDetail, TaskCard, AssignedCards, BoardColumn,
        ProjectSettings, BoardSettings, ExtensionSettings, AppBar, TaskMenu, Page,
    ];

    /// <summary>
    /// The components loaded extensions contribute to a seam, in the order core
    /// draws them: by the attribute's order, then by extension id. Extensions
    /// the app draws itself are left out — it has those already.
    /// </summary>
    public static IReadOnlyList<KitContribution> Find(string seam, ExtensionCatalog catalog, IReadOnlySet<string> skip) => seam switch
    {
        ProjectCard => Find<ProjectCardSectionAttribute>(catalog, skip, a => a.Title, a => a.Order),
        ProjectHome => Find<ProjectHomeSectionAttribute>(catalog, skip, a => a.Title, a => a.Order),
        ProjectNav => Find<ProjectNavSectionAttribute>(catalog, skip, a => a.Title, a => a.Order),
        Dashboard => Find<DashboardSectionAttribute>(catalog, skip, a => a.Title, a => a.Order),
        TaskDetail => Find<TaskDetailExtensionAttribute>(catalog, skip, _ => string.Empty, _ => 0),
        TaskCard or AssignedCards => Find<TaskCardExtensionAttribute>(catalog, skip, _ => string.Empty, a => a.Order),
        BoardColumn => Find<BoardColumnExtensionAttribute>(catalog, skip, _ => string.Empty, a => a.Order),
        ProjectSettings => Find<ProjectSettingsSectionAttribute>(catalog, skip, a => a.Title, a => a.Order),
        BoardSettings => Find<BoardSettingsSectionAttribute>(catalog, skip, a => a.Title, a => a.Order),
        ExtensionSettings => Find<ExtensionSettingsAttribute>(catalog, skip, _ => string.Empty, _ => 0),
        AppBar => Find<AppBarExtensionAttribute>(catalog, skip, _ => string.Empty, a => a.Order),
        TaskMenu => MenuContributors(catalog, skip),
        Page => KitPages.Components(catalog, skip).Select(p => new KitContribution(p.ExtensionId, string.Empty, 0, p.Component)).ToList(),
        _ => [],
    };

    /// <summary>
    /// The classes that add items to a task's menu, in core's order. The
    /// "component" of each is the contributor's class, which the surface
    /// creates and asks rather than renders.
    /// </summary>
    public static IReadOnlyList<KitContribution> MenuContributors(ExtensionCatalog catalog, IReadOnlySet<string> skip) =>
        catalog.Loaded
            .Where(e => !skip.Contains(e.Descriptor.Id))
            .SelectMany(e => e.Assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISoloTaskMenuContributor).IsAssignableFrom(t))
                .Select(t => (e.Descriptor.Id, Type: t, Attribute: t.GetCustomAttribute<TaskMenuContributorAttribute>()))
                .Where(c => c.Attribute is not null))
            .OrderBy(c => c.Attribute!.Order)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new KitContribution(c.Id, string.Empty, c.Attribute!.Order, c.Type))
            .ToList();

    private static IReadOnlyList<KitContribution> Find<TAttribute>(
        ExtensionCatalog catalog, IReadOnlySet<string> skip, Func<TAttribute, string> title, Func<TAttribute, int> order)
        where TAttribute : Attribute =>
        catalog.Loaded
            .Where(e => !skip.Contains(e.Descriptor.Id))
            .SelectMany(e => e.Assembly.GetExportedTypes()
                .Where(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t))
                .Select(t => (e.Descriptor.Id, Type: t, Attribute: t.GetCustomAttribute<TAttribute>()))
                .Where(c => c.Attribute is not null))
            .OrderBy(c => order(c.Attribute!))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new KitContribution(c.Id, title(c.Attribute!), order(c.Attribute!), c.Type))
            .ToList();
}
