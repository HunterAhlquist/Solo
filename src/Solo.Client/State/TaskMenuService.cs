using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solo.Client.Extensions;
using Solo.Shared.Extensibility;

namespace Solo.Client.State;

/// <summary>
/// Core's menu for a task: its own items, then what each extension's
/// <see cref="ISoloTaskMenuContributor"/> offers, shown through the host
/// context menu so the desktop app gets a native one.
/// </summary>
/// <remarks>
/// <para>Contributors are classes in loaded extension assemblies, found by
/// attribute and created from this scope's services for each menu. A client
/// half can't register services, so this is how one gets constructor
/// injection at all.</para>
///
/// <para>Each contributor's ids are prefixed with its position before they
/// reach the menu, so two extensions can both say <c>"open"</c>. One that
/// throws or is slow costs only its own items: the menu is never held hostage
/// by an extension.</para>
/// </remarks>
public sealed class TaskMenuService(
    ExtensionCatalog catalog,
    ContextMenuService menu,
    IServiceProvider services,
    ILogger<TaskMenuService> logger)
{
    private static readonly TimeSpan ItemsTimeout = TimeSpan.FromSeconds(1);

    private IReadOnlyList<Type>? _contributors;
    private int _cachedFor = -1;

    /// <summary>
    /// True when any loaded extension contributes. A board only takes over the
    /// right click on its cards when this is, so with none installed the
    /// browser's own menu is untouched.
    /// </summary>
    public bool HasContributors => Contributors.Count > 0;

    private IReadOnlyList<Type> Contributors
    {
        get
        {
            // The catalog only grows, once, after sign-in; its count is enough to
            // notice that without subscribing to it from a service.
            if (_contributors is null || _cachedFor != catalog.Loaded.Count)
            {
                _contributors = ContributedSections.Implementations<TaskMenuContributorAttribute, ISoloTaskMenuContributor>(
                    catalog, a => a.Order);
                _cachedFor = catalog.Loaded.Count;
            }

            return _contributors;
        }
    }

    /// <summary>
    /// Shows the menu at a viewport point and runs whatever is chosen.
    /// <paramref name="coreItems"/> come first, each with what choosing it does.
    /// With nothing to show at all, it says so in a single disabled item rather
    /// than opening nothing where the person clicked.
    /// </summary>
    public async Task ShowAsync(
        double clientX, double clientY, TaskMenuContext context,
        IReadOnlyList<(ContextMenuItem Item, Func<Task> Run)>? coreItems = null)
    {
        var instances = new List<ISoloTaskMenuContributor>();
        try
        {
            var items = new List<ContextMenuItem>();
            var actions = new Dictionary<string, Func<Task>>();
            foreach (var (item, run) in coreItems ?? [])
            {
                var id = $"core:{item.Id}";
                items.Add(item with { Id = id });
                actions[id] = run;
            }

            for (var index = 0; index < Contributors.Count; index++)
            {
                if (Create(Contributors[index]) is not { } contributor)
                {
                    continue;
                }

                instances.Add(contributor);
                var offered = await ItemsFromAsync(contributor, context);
                for (var i = 0; i < offered.Count; i++)
                {
                    var own = offered[i];
                    var id = $"{index}:{own.Id}";
                    // Each extension's items are a group of their own.
                    items.Add(own with { Id = id, SeparatorBefore = own.SeparatorBefore || (i == 0 && items.Count > 0) });
                    actions[id] = () => contributor.OnChosenAsync(own.Id, context);
                }
            }

            if (items.Count == 0)
            {
                items.Add(new ContextMenuItem("none", "No actions for this task") { Disabled = true });
            }

            var chosen = await menu.ShowAsync(clientX, clientY, items);
            if (chosen is not null && actions.TryGetValue(chosen, out var action))
            {
                await action();
            }
        }
        finally
        {
            foreach (var instance in instances)
            {
                await DisposeAsync(instance);
            }
        }
    }

    private ISoloTaskMenuContributor? Create(Type type)
    {
        try
        {
            return (ISoloTaskMenuContributor)ActivatorUtilities.CreateInstance(services, type);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Couldn't create task menu contributor {Type}.", type.FullName);
            return null;
        }
    }

    private async Task<IReadOnlyList<ContextMenuItem>> ItemsFromAsync(
        ISoloTaskMenuContributor contributor, TaskMenuContext context)
    {
        using var timeout = new CancellationTokenSource(ItemsTimeout);
        try
        {
            // WaitAsync as well as the token: a contributor that ignores it still
            // doesn't get to hold the menu open.
            return await contributor.ItemsAsync(context, timeout.Token).AsTask().WaitAsync(ItemsTimeout);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Task menu contributor {Type} offered nothing: it failed or was too slow.",
                contributor.GetType().FullName);
            return [];
        }
    }

    private static async ValueTask DisposeAsync(object instance)
    {
        switch (instance)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
