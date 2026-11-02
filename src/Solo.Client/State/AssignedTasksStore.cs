using Solo.Client.Api;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.State;

/// <summary>
/// The Your Tasks dashboard's payload: the signed-in user's tasks across every
/// project, kept current from core's own user topic the way
/// <see cref="BoardStore"/> keeps a board current from its group.
/// </summary>
/// <remarks>
/// <para>Its own store rather than the board's: the dashboard spans boards, and
/// the board store, its hub group and its presence are built around one board
/// at a time. The task drawer still borrows the board store when a card here is
/// opened, so the two coexist — this one hears a drawer save through
/// <see cref="TaskDrawerService.Changed"/>, not through the board.</para>
///
/// <para>A nudge names an entity, never a value. A task refetches itself, which
/// also tells us whether it is still ours; anything structural — a board
/// renamed, a label recoloured, a project left — debounces into a full
/// refetch, the one-shot payload being self-consistent by construction.</para>
/// </remarks>
public sealed class AssignedTasksStore(ApiClient api, ISoloLiveTopics live, UserSession session) : IAsyncDisposable
{
    private static readonly TimeSpan RefetchDebounce = TimeSpan.FromMilliseconds(150);

    private IAsyncDisposable? _subscription;
    private CancellationTokenSource? _debounce;
    private Task _pendingRefetch = Task.CompletedTask;

    public AssignedTasksDto? Work { get; private set; }

    /// <summary>The payload changed; subscribers re-render.</summary>
    public event Action? Changed;

    /// <summary>A remote change just landed on this task; its card flashes once.</summary>
    public event Action<Guid>? Pulsed;

    /// <summary>The in-flight debounced refetch; a completed task while idle. Tests await it.</summary>
    public Task PendingRefetch => _pendingRefetch;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Work = await api.GetAssignedTasksAsync(ct);
        _subscription ??= await live.SubscribeUserAsync(BoardHubConstants.AssignedTasksTopic, OnNudgeAsync);
        Changed?.Invoke();
    }

    /// <summary>Re-reads everything. Also the 409 recovery path: refetch, replace, move on.</summary>
    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Work is null)
        {
            return;
        }

        Work = await api.GetAssignedTasksAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies the DTO a mutation or a refetch returned. A task that is no
    /// longer the user's leaves; one that landed on a board this payload has
    /// never seen can't be drawn from it, so the whole payload reloads instead.
    /// </summary>
    public void ApplyTask(TaskDto task)
    {
        if (Work is null)
        {
            return;
        }

        if (session.UserId is not { } me || !task.AssigneeIds.Contains(me))
        {
            RemoveTask(task.Id);
            return;
        }

        if (Work.Boards.All(b => b.Id != task.BoardId))
        {
            ScheduleRefetch();
            return;
        }

        var tasks = Work.Tasks.ToList();
        var index = tasks.FindIndex(t => t.Id == task.Id);
        if (index >= 0)
        {
            tasks[index] = task;
        }
        else
        {
            tasks.Add(task);
        }

        Work = Work with { Tasks = tasks };
        Changed?.Invoke();
    }

    public void RemoveTask(Guid taskId)
    {
        if (Work is null || Work.Tasks.All(t => t.Id != taskId))
        {
            return;
        }

        Work = Work with { Tasks = Work.Tasks.Where(t => t.Id != taskId).ToList() };
        Changed?.Invoke();
    }

    private async Task OnNudgeAsync(TopicMessage message)
    {
        if (Work is null || !Guid.TryParse(message.Data, out var entityId))
        {
            return;
        }

        switch (message.Kind)
        {
            case EntityTypes.Task:
                await RefetchTaskAsync(entityId);
                break;

            case EntityTypes.SubTask:
                // The nudge names the sub-task; the card draws its parent. One we
                // have never seen belongs to a task that just became ours, or to
                // nobody we show — the payload decides.
                var parent = Work.Tasks.FirstOrDefault(t => t.SubTasks.Any(s => s.Id == entityId));
                if (parent is not null)
                {
                    await RefetchTaskAsync(parent.Id);
                }
                else
                {
                    ScheduleRefetch();
                }

                break;

            default:
                ScheduleRefetch();
                break;
        }
    }

    private async Task RefetchTaskAsync(Guid taskId)
    {
        try
        {
            ApplyTask(await api.GetTaskAsync(taskId));
            Pulsed?.Invoke(taskId);
        }
        catch (ApiException)
        {
            // Deleted, or on a board we may no longer read: either way it is not
            // ours to show.
            RemoveTask(taskId);
        }
    }

    private void ScheduleRefetch()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        var token = cts.Token;

        _pendingRefetch = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RefetchDebounce, token);
                await RefreshAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later change; that one refetches for both.
            }
            catch (ApiException)
            {
                // Signed out, or offline — the next interaction reports it.
            }
        });
    }

    /// <summary>Stops listening and clears the payload; the page calls this when it leaves.</summary>
    public async Task DetachAsync()
    {
        if (_subscription is { } subscription)
        {
            _subscription = null;
            await subscription.DisposeAsync();
        }

        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;

        await _pendingRefetch;
        _pendingRefetch = Task.CompletedTask;
        Work = null;
    }

    public async ValueTask DisposeAsync() => await DetachAsync();
}
