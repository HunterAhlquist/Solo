using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Solo.Client.Api;
using Solo.Client.Realtime;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.State;

/// <summary>
/// The host side of <see cref="ISoloTaskDrawer"/>: opens the board's task drawer
/// over a page that isn't a board. <c>TaskDrawerHost</c> renders what this holds.
/// </summary>
/// <remarks>
/// <para>The drawer reads members, labels and presence off <see cref="BoardStore"/>,
/// and the hub follows one board at a time, so this borrows the app's one store
/// rather than keeping a second that would fight it over the hub. It loads the
/// task's board only when no board page holds the store, and detaches only what
/// it attached — a board page is never left pointing at nothing.</para>
///
/// <para>Saves are read off the store rather than reported by the drawer, so a
/// checklist tick, an assignee change or a remote edit all reach
/// <see cref="Changed"/> the same way. Deletes and moves are reported by the
/// drawer itself, before the card leaves the store — only it knows which of the
/// two just happened.</para>
/// </remarks>
public sealed class TaskDrawerService(
    ApiClient api, BoardStore store, UserSession session, IBoardHubClient hub, NavigationManager navigation)
    : ISoloTaskDrawer, IDisposable
{
    private bool _ownsStore;
    private bool _subscribed;
    private bool _departed;

    /// <summary>The task the drawer shows; null while closed.</summary>
    public TaskDto? Task { get; private set; }

    public bool CanWrite { get; private set; }

    public bool CanModerate { get; private set; }

    public bool CanComment { get; private set; }

    /// <summary>What the host renders changed.</summary>
    public event Action? StateChanged;

    public event Action<TaskDrawerChange>? Changed;

    public event Action? Closed;

    public async Task OpenAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await api.GetTaskAsync(taskId, ct);
        if (session.Current is null)
        {
            await session.LoadAsync(ct);
        }

        if (store.Board is { } held && held.Id != task.BoardId && !_ownsStore)
        {
            // A board page owns the store and is showing another board. Repointing
            // it would pull that page out from under its user; the task's own board
            // has a drawer of its own, so go there, the way a task link does.
            var destination = await api.GetBoardAsync(task.BoardId, ct);
            navigation.NavigateTo(
                $"projects/{destination.ProjectAlias}/boards/{destination.Alias}?task={task.Number}");
            return;
        }

        if (store.Board?.Id != task.BoardId)
        {
            await store.LoadAsync(task.BoardId, ct);
            _ownsStore = true;
        }

        if (!_subscribed)
        {
            store.Changed += OnStoreChanged;
            navigation.LocationChanged += OnLocationChanged;
            _subscribed = true;
        }

        var board = store.Board!;
        _departed = false;
        Task = board.Tasks.FirstOrDefault(t => t.Id == taskId) ?? task;

        // The board page's rules: owners moderate comments, writers edit, and a
        // closed board is read-only for everyone, comments included.
        (CanModerate, var canWrite) = session.AccessIn(store.Members);
        CanWrite = canWrite && board.IsOpen;
        CanComment = board.IsOpen;

        await hub.SetPresence(EntityTypes.Task, taskId, PresenceMode.Viewing);
        StateChanged?.Invoke();
    }

    /// <summary>The drawer's own delete or move, announced before the card leaves the store.</summary>
    public void Report(TaskDrawerChange change)
    {
        _departed = true;
        Changed?.Invoke(change);
    }

    public async Task CloseAsync()
    {
        if (Task is null)
        {
            return;
        }

        Task = null;
        Unsubscribe();
        if (_ownsStore)
        {
            _ownsStore = false;
            await store.DetachAsync();
        }
        else if (store.BoardId is { } boardId)
        {
            await hub.SetPresence(EntityTypes.Board, boardId, PresenceMode.None);
        }

        StateChanged?.Invoke();
        Closed?.Invoke();
    }

    private void OnStoreChanged()
    {
        if (Task is not { } shown)
        {
            return;
        }

        var current = store.Board?.Tasks.FirstOrDefault(t => t.Id == shown.Id);
        if (current is null)
        {
            // Gone from its board without the drawer saying why: deleted or moved
            // by someone else. Only a fresh read can tell those apart.
            if (!_departed)
            {
                _departed = true;
                _ = DepartedRemotelyAsync(shown.Id);
            }

            return;
        }

        Task = current;
        if (Differs(shown, current))
        {
            Changed?.Invoke(new TaskDrawerChange(TaskDrawerChangeKind.Saved, current.Id, current));
        }

        StateChanged?.Invoke();
    }

    private async Task DepartedRemotelyAsync(Guid taskId)
    {
        TaskDto? moved = null;
        try
        {
            moved = await api.GetTaskAsync(taskId);
        }
        catch (ApiException)
        {
            // Deleted, or moved somewhere this user can't read — the same to a caller.
        }

        Changed?.Invoke(moved is null
            ? new TaskDrawerChange(TaskDrawerChangeKind.Deleted, taskId, null)
            : new TaskDrawerChange(TaskDrawerChangeKind.Moved, taskId, moved));
        await CloseAsync();
    }

    /// <summary>
    /// Leaving the page closes the drawer. A board page reloads the store for
    /// itself, so heading to one hands the store over rather than detaching it
    /// out from under the page that is about to render.
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (navigation.ToBaseRelativePath(e.Location).Split('/', StringSplitOptions.RemoveEmptyEntries)
            is ["projects", _, "boards", ..])
        {
            _ownsStore = false;
        }

        _ = CloseAsync();
    }

    /// <summary>
    /// Assignees and sub-tasks live in rows of their own, so the task's version
    /// doesn't move when they do; a board refetch hands back equal tasks as new
    /// instances, which mustn't read as edits.
    /// </summary>
    private static bool Differs(TaskDto a, TaskDto b) =>
        a.Version != b.Version ||
        !a.AssigneeIds.SequenceEqual(b.AssigneeIds) ||
        !a.SubTasks.Select(s => (s.Id, s.Version)).SequenceEqual(b.SubTasks.Select(s => (s.Id, s.Version)));

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        store.Changed -= OnStoreChanged;
        navigation.LocationChanged -= OnLocationChanged;
        _subscribed = false;
    }

    public void Dispose() => Unsubscribe();
}
