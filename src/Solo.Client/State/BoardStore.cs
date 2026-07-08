using Solo.Client.Api;
using Solo.Client.Realtime;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.State;

/// <summary>
/// Holds the board's render payload plus the live presence snapshot, and turns
/// hub change events into refetches.
///
/// Change events carry field <em>names</em>, never values, so there is nothing to
/// patch from them — the store refetches. Task events refetch that one task;
/// everything structural debounces into a full <c>GET /api/boards/{id}</c>,
/// which is the one-shot render payload and therefore always self-consistent.
/// No Fluxor: the save-event model keeps mutation paths few.
/// </summary>
public sealed class BoardStore(ApiClient api, IBoardHubClient hub, UserSession session) : IAsyncDisposable
{
    private static readonly TimeSpan RefetchDebounce = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _debounce;
    private bool _subscribed;

    public BoardDetailDto? Board { get; private set; }

    public IReadOnlyList<ProjectMemberDto> Members { get; private set; } = [];

    public IReadOnlyList<PresenceEntry> Presence { get; private set; } = [];

    public Guid? BoardId => Board?.Id;

    /// <summary>The board payload changed; subscribers re-render.</summary>
    public event Action? Changed;

    /// <summary>
    /// A remote change just landed on this entity. The affected card or drawer
    /// section flashes its outline once — activity is visible without a toast.
    /// </summary>
    public event Action<string, Guid>? Pulsed;

    /// <summary>A comment was added, edited, or removed somewhere on this board.</summary>
    public event Action? CommentsChanged;

    public async Task LoadAsync(Guid boardId, CancellationToken ct = default)
    {
        Board = await api.GetBoardAsync(boardId, ct);
        Members = await api.GetMembersAsync(Board.ProjectId, ct);

        if (!_subscribed)
        {
            hub.EntityChanged += OnEntityChangedAsync;
            hub.PresenceChanged += OnPresenceChangedAsync;
            _subscribed = true;
        }

        await hub.JoinBoard(boardId);
        Changed?.Invoke();
    }

    /// <summary>Re-reads the whole board. Also the 409 recovery path: refetch, replace, move on.</summary>
    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Board is null)
        {
            return;
        }

        Board = await api.GetBoardAsync(Board.Id, ct);
        Changed?.Invoke();
    }

    /// <summary>Applies the DTO a mutation returned, so the local view matches the server without a refetch.</summary>
    public void ApplyTask(TaskDto task)
    {
        if (Board is null)
        {
            return;
        }

        var tasks = Board.Tasks.ToList();
        var index = tasks.FindIndex(t => t.Id == task.Id);
        if (index >= 0)
        {
            tasks[index] = task;
        }
        else
        {
            tasks.Add(task);
        }

        Board = Board with { Tasks = tasks };
        Changed?.Invoke();
    }

    public void ApplyEpic(EpicDto epic)
    {
        if (Board is null)
        {
            return;
        }

        var epics = Board.Epics.ToList();
        var index = epics.FindIndex(e => e.Id == epic.Id);
        if (index >= 0)
        {
            epics[index] = epic;
        }
        else
        {
            epics.Add(epic);
        }

        Board = Board with { Epics = epics.OrderBy(e => e.Rank).ToList() };
        Changed?.Invoke();
    }

    public void RemoveTask(Guid taskId)
    {
        if (Board is null)
        {
            return;
        }

        Board = Board with { Tasks = Board.Tasks.Where(t => t.Id != taskId).ToList() };
        Changed?.Invoke();
    }

    // -- Presence -----------------------------------------------------------

    /// <summary>Everyone but you who is on this entity — you already know you're here.</summary>
    public IReadOnlyList<PresenceEntry> PresenceOn(string entityType, Guid entityId) =>
        Presence
            .Where(p => p.EntityType == entityType && p.EntityId == entityId && p.UserId != session.UserId)
            .ToList();

    /// <summary>
    /// Other people actively editing this entity. A non-empty result is what the
    /// editor shows as "Hunter is editing this" before you type a character.
    /// </summary>
    public IReadOnlyList<PresenceEntry> EditorsOn(string entityType, Guid entityId) =>
        PresenceOn(entityType, entityId).Where(p => p.Mode == PresenceMode.Editing).ToList();

    // -- Change feed --------------------------------------------------------

    private async Task OnEntityChangedAsync(EntityChangedEvent change)
    {
        if (Board is null)
        {
            return;
        }

        switch (change.EntityType)
        {
            case EntityTypes.Task when change.Kind == ChangeKind.Deleted:
                RemoveTask(change.EntityId);
                break;

            case EntityTypes.Task:
                await RefetchTaskAsync(change.EntityId);
                break;

            case EntityTypes.SubTask:
                // The event carries the sub-task's id, but the board renders its
                // parent. When we already know the parent, refetch just that task
                // (its status may have rolled up); a sub-task we've never seen
                // means the parent is stale too, so fall back to the board.
                var parent = Board.Tasks.FirstOrDefault(t => t.SubTasks.Any(s => s.Id == change.EntityId));
                if (parent is not null)
                {
                    await RefetchTaskAsync(parent.Id);
                }
                else
                {
                    ScheduleBoardRefetch(change);
                }

                break;

            case EntityTypes.Comment:
                // Comments aren't part of BoardDetailDto, so refetching the board
                // would tell us nothing. Only an open drawer cares.
                CommentsChanged?.Invoke();
                Pulsed?.Invoke(change.EntityType, change.EntityId);
                break;

            default:
                // epic, label, board, project — structural, so take the one-shot
                // payload and be consistent by construction.
                ScheduleBoardRefetch(change);
                break;
        }
    }

    private async Task RefetchTaskAsync(Guid taskId)
    {
        try
        {
            ApplyTask(await api.GetTaskAsync(taskId));
            Pulsed?.Invoke(EntityTypes.Task, taskId);
        }
        catch (ApiException)
        {
            // Deleted between the broadcast and our refetch, or access just
            // revoked. The board refetch below reconciles either way.
            await RefreshAsync();
        }
    }

    private void ScheduleBoardRefetch(EntityChangedEvent change)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = new CancellationTokenSource();
        _debounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RefetchDebounce, cts.Token);
                await RefreshAsync(cts.Token);
                Pulsed?.Invoke(change.EntityType, change.EntityId);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later change; that one refetches for both.
            }
            catch (ApiException)
            {
                // Board gone or access revoked — the page's own navigation guard
                // handles it on the next interaction.
            }
        });
    }

    private Task OnPresenceChangedAsync(BoardPresenceSnapshot snapshot)
    {
        if (snapshot.BoardId == Board?.Id)
        {
            Presence = snapshot.Entries;
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Leaves the board and clears state. The store is scoped — which in WASM
    /// means one instance for the app's lifetime — so the board page calls this
    /// when it navigates away, and <see cref="LoadAsync"/> re-attaches later.
    /// </summary>
    public async Task DetachAsync()
    {
        if (_subscribed)
        {
            hub.EntityChanged -= OnEntityChangedAsync;
            hub.PresenceChanged -= OnPresenceChangedAsync;
            _subscribed = false;
        }

        if (Board is not null)
        {
            await hub.LeaveBoard(Board.Id);
        }

        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
        Board = null;
        Members = [];
        Presence = [];
    }

    public async ValueTask DisposeAsync() => await DetachAsync();
}
