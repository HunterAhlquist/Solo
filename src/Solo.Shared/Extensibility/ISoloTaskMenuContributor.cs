using Solo.Shared.Dtos;

namespace Solo.Shared.Extensibility;

/// <summary>
/// Adds items to the menu core shows for a task: right-clicking its card on a
/// board, or the ⋯ button in its drawer, which is how a touchscreen reaches the
/// same items (contract 1.8).
/// </summary>
/// <remarks>
/// <para>A class, not a component: the host asks it for items each time the
/// menu opens and hands it the choice. Mark it with
/// <see cref="TaskMenuContributorAttribute"/>. The host creates it from its own
/// services for each menu, so its constructor can take
/// <see cref="ISoloClientApi"/>, <see cref="ISoloTaskPicker"/>, MudBlazor's
/// <c>IDialogService</c> and the rest — a client half can't register services
/// of its own.</para>
///
/// <para>Core's items come first, then each contributor's in order, every group
/// after a divider. Item ids are yours alone: the host keeps two contributors'
/// <c>"open"</c> apart. A board takes over the right click on its cards only
/// while some extension contributes; with none installed, it is the browser's
/// own.</para>
///
/// <para>Like <see cref="ISoloContextMenu"/>, the menu checks nothing: leave out
/// what the person can't do, and let your endpoint refuse the rest.</para>
/// </remarks>
public interface ISoloTaskMenuContributor
{
    /// <summary>
    /// Items for this task, or none. Called as the menu opens and awaited before
    /// it shows, so answer from what you already hold. A contributor that throws
    /// or takes longer than a second is left out of that menu.
    /// </summary>
    ValueTask<IReadOnlyList<ContextMenuItem>> ItemsAsync(TaskMenuContext context, CancellationToken ct);

    /// <summary>Runs when the person chooses one of this contributor's items, with that item's own id.</summary>
    Task OnChosenAsync(string itemId, TaskMenuContext context);
}

/// <summary>Marks an <see cref="ISoloTaskMenuContributor"/> for the host to find.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TaskMenuContributorAttribute : Attribute
{
    /// <summary>Ascending among contributors; ties break by extension id.</summary>
    public int Order { get; set; }
}

/// <summary>The task a menu is for.</summary>
/// <param name="CanWrite">
/// The board's own test for allowing edits: write access to the project, and an
/// open board.
/// </param>
public sealed record TaskMenuContext(TaskDto Task, Guid ProjectId, bool CanWrite);
