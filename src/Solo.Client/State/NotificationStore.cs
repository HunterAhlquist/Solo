using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Client.Api;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.State;

/// <summary>
/// The signed-in person's inbox, kept current from core's notifications topic,
/// and the one place that turns a new entry into an OS notification or a
/// snackbar.
/// </summary>
/// <remarks>
/// <para>A nudge only says "refetch": the inbox it then reads is the truth, so a
/// message that names nothing new (a reconnect, a nudge from somewhere else on
/// the same topic) alerts nobody. Only an unread entry that wasn't in an earlier
/// read is announced, so a backlog waiting at load shows on the bell rather than
/// as a burst of alerts. An entry is known by its id and raise time together: a
/// re-raised assignment keeps its id and comes back as news.</para>
///
/// <para>The browser's time zone is reported the first time this loads without
/// one on file: deadline reminders go out at 9:00 the person's time, and they
/// shouldn't have to find a setting for that to be right.</para>
///
/// <para>In the iPhone and iPad app, which shows notifications while it's
/// closed only if they were scheduled ahead, every refresh also hands it the
/// deadline reminders still to come (<c>api/notifications/upcoming</c>), and
/// so does the page coming back into view. A change made elsewhere reaches the
/// schedule at the next of those.</para>
/// </remarks>
public sealed class NotificationStore(
    ApiClient api,
    ISoloLiveTopics live,
    ISoloTaskDrawer drawer,
    NavigationManager navigation,
    IJSRuntime js,
    ISnackbar snackbar,
    TimeProvider time,
    ILogger<NotificationStore> logger) : IAsyncDisposable
{
    private readonly HashSet<(Guid, DateTimeOffset)> _known = [];
    private IAsyncDisposable? _subscription;
    private DotNetObjectReference<NotificationStore>? _self;
    private bool _canSchedule;

    public NotificationInboxDto? Inbox { get; private set; }

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_subscription is not null)
        {
            return;
        }

        _self = DotNetObjectReference.Create(this);
        _subscription = await live.SubscribeUserAsync(BoardHubConstants.NotificationsTopic, _ => RefreshAsync());
        live.ConnectionChanged += OnConnectionChangedAsync;
        try
        {
            _canSchedule = await js.InvokeAsync<bool>("solo.notifications.canSchedule", ct);
        }
        catch (JSException e)
        {
            logger.LogWarning(e, "Couldn't tell whether this device schedules notifications.");
        }

        // Before the first read: the app hands over a tap that launched it on
        // the first call this page makes to it, which the read's scheduling
        // would otherwise be, before anything listens. Opening one reads the
        // inbox itself.
        try
        {
            await js.InvokeAsync<bool>("solo.notifications.attach", ct, _self);
        }
        catch (JSException e)
        {
            logger.LogWarning(e, "Couldn't listen for notification taps.");
        }

        await RefreshAsync();
        await ReportTimeZoneAsync(ct);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var inbox = await api.GetNotificationsAsync();
            var fresh = Inbox is null
                ? []
                : inbox.Items.Where(n => n.ReadAt is null && !_known.Contains((n.Id, n.CreatedAt))).ToList();
            foreach (var item in inbox.Items)
            {
                _known.Add((item.Id, item.CreatedAt));
            }

            Inbox = inbox;
            Changed?.Invoke();
            foreach (var item in fresh)
            {
                await AnnounceAsync(item);
            }

            await ScheduleRemindersAsync();
        }
        catch (Exception e) when (e is HttpRequestException or ApiException or TaskCanceledException)
        {
            // The bell keeps what it had; the next nudge or reconnect tries again.
            logger.LogWarning(e, "Couldn't refresh notifications.");
        }
    }

    /// <summary>Marks it read and takes the person to what it's about.</summary>
    public async Task OpenAsync(NotificationDto notification)
    {
        if (notification.ReadAt is null)
        {
            await MarkReadAsync(notification);
        }

        if (notification.TaskId is { } taskId)
        {
            await drawer.OpenAsync(taskId);
        }
        else if (notification.BoardId is { } boardId)
        {
            var board = await api.GetBoardAsync(boardId);
            navigation.NavigateTo($"projects/{board.ProjectAlias}/boards/{board.Alias}");
        }
    }

    public async Task MarkAllReadAsync()
    {
        await api.MarkAllNotificationsReadAsync();
        await RefreshAsync();
    }

    [JSInvokable]
    public async Task OpenFromNotification(string id)
    {
        if (Inbox?.Items.FirstOrDefault(n => n.Id.ToString() == id) is null)
        {
            // Raised after the last read, such as one the app showed while closed.
            await RefreshAsync();
        }

        if (Inbox?.Items.FirstOrDefault(n => n.Id.ToString() == id) is { } notification)
        {
            await OpenAsync(notification);
        }
    }

    /// <summary>
    /// A tapped deadline reminder the app scheduled ahead: it names the task,
    /// not an entry, since the server raises the entry only when it's due. Any
    /// unread entry for the task is what the person just read.
    /// </summary>
    [JSInvokable]
    public async Task OpenTaskFromNotification(string taskId)
    {
        if (!Guid.TryParse(taskId, out var id))
        {
            return;
        }

        await RefreshAsync();
        foreach (var notification in Inbox?.Items.Where(n => n.TaskId == id && n.ReadAt is null).ToList() ?? [])
        {
            await MarkReadAsync(notification);
        }

        await drawer.OpenAsync(id);
    }

    /// <summary>Back in view, perhaps after a while away: catch up, and in the app reschedule.</summary>
    [JSInvokable]
    public Task PageVisible() => RefreshAsync();

    private async Task ScheduleRemindersAsync()
    {
        if (!_canSchedule)
        {
            return;
        }

        try
        {
            var reminders = await api.GetUpcomingRemindersAsync();
            await js.InvokeAsync<int>("solo.notifications.schedule", reminders);
        }
        catch (Exception e) when (e is HttpRequestException or ApiException or TaskCanceledException or JSException)
        {
            // What was scheduled before stays; the next refresh tries again.
            logger.LogWarning(e, "Couldn't schedule deadline reminders on this device.");
        }
    }

    private async Task MarkReadAsync(NotificationDto notification)
    {
        await api.MarkNotificationReadAsync(notification.Id);
        if (Inbox is { } inbox)
        {
            var now = time.GetUtcNow();
            Inbox = inbox with
            {
                Items = inbox.Items.Select(n => n.Id == notification.Id ? n with { ReadAt = now } : n).ToList(),
                UnreadCount = Math.Max(0, inbox.UnreadCount - 1),
            };
            Changed?.Invoke();
        }
    }

    private async Task AnnounceAsync(NotificationDto notification)
    {
        try
        {
            if (!await js.InvokeAsync<bool>("solo.notifications.attended") &&
                await js.InvokeAsync<bool>("solo.notifications.show",
                    notification.Id.ToString(), notification.Title, notification.Body, _self))
            {
                return;
            }
        }
        catch (JSException e)
        {
            logger.LogWarning(e, "The browser refused an OS notification; showing it in the page.");
        }

        snackbar.Add(notification.Title, Severity.Info, config =>
        {
            config.Action = "Open";
            config.OnClick = _ => OpenAsync(notification);
        });
    }

    private async Task ReportTimeZoneAsync(CancellationToken ct)
    {
        try
        {
            var preferences = await api.GetNotificationPreferencesAsync(ct);
            if (preferences.TimeZone is not null)
            {
                return;
            }

            var zone = await js.InvokeAsync<string?>("solo.notifications.timeZone", ct);
            if (string.IsNullOrEmpty(zone))
            {
                return;
            }

            await api.UpdateNotificationPreferencesAsync(new UpdateNotificationPreferencesRequest
            {
                TimeZone = zone,
                NotifyOnAssigned = preferences.NotifyOnAssigned,
                NotifyOnDeadline = preferences.NotifyOnDeadline,
                EmailOnAssigned = preferences.EmailOnAssigned,
                EmailOnDeadline = preferences.EmailOnDeadline,
                DigestFrequency = preferences.DigestFrequency,
                DigestHour = preferences.DigestHour,
                DigestWeekday = preferences.DigestWeekday,
                Version = preferences.Version,
            }, ct);
        }
        catch (Exception e) when (e is HttpRequestException or ApiException or JSException or TaskCanceledException)
        {
            // UTC until the next load gets it through; nothing the person can act on.
            logger.LogWarning(e, "Couldn't report this browser's time zone.");
        }
    }

    private Task OnConnectionChangedAsync() => live.IsConnected ? RefreshAsync() : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        live.ConnectionChanged -= OnConnectionChangedAsync;
        try
        {
            await js.InvokeVoidAsync("solo.notifications.detach");
        }
        catch (Exception e) when (e is JSException or JSDisconnectedException or TaskCanceledException)
        {
            // The page is going away with its listeners.
        }

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }

        _self?.Dispose();
    }
}
