using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using Solo.Client.Api;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Client.Realtime;

/// <summary>
/// Typed wrapper over the <c>/hubs/board</c> connection, implementing the shared
/// <see cref="IBoardHubServer"/> contract and surfacing <see cref="IBoardClient"/>
/// callbacks as C# events. Survives reconnects by rejoining the board and the
/// extension topics it holds, and replaying the last presence reports.
/// </summary>
/// <remarks>
/// The hub protocol is left at ASP.NET's default JSON settings — the server does
/// not add the string-enum converter to SignalR, only to MVC — so enums travel
/// as numbers here. Do not hand it <see cref="SoloJson.Options"/>.
/// </remarks>
public sealed class BoardHubClient(
    InstanceConnection instance,
    ApiClient api,
    ConnectionIdAccessor connections,
    Solo.Client.Offline.OfflineCache? offline = null,
    IJSRuntime? js = null)
    : IBoardHubClient, IAsyncDisposable
{
    private DotNetObjectReference<BoardHubClient>? _self;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
    private Timer? _retry;
    private bool _disposed;
    private Guid? _boardId;
    private (string EntityType, Guid EntityId, PresenceMode Mode)? _presence;

    /// <summary>
    /// Topics held, counted: a page and its sidebar section can share one, and
    /// the subscription goes only when the last of them lets go.
    /// </summary>
    private readonly Dictionary<(Guid ProjectId, string Topic), (int Holders, PresenceMode Mode)> _topics = [];

    /// <summary>The signed-in user's own topics held, counted the same way.</summary>
    private readonly Dictionary<string, int> _userTopics = [];

    /// <summary>A change landed from another client. Field names only — refetch to get values.</summary>
    public event Func<EntityChangedEvent, Task>? EntityChanged;

    /// <summary>Full presence snapshot for the joined board.</summary>
    public event Func<BoardPresenceSnapshot, Task>? PresenceChanged;

    public event Func<TopicMessage, Task>? TopicMessage;

    public event Func<TopicPresenceSnapshot, Task>? TopicPresenceChanged;

    /// <summary>Raised when the connection state changes, so the UI can show a "reconnecting" hint.</summary>
    public event Func<Task>? ConnectionStateChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public bool IsActive => _boardId is not null || _topics.Values.Any(t => t.Mode != PresenceMode.None);

    public async Task<LiveSendResult> SendTopicMessage(Guid projectId, string topic, string kind, string? data)
    {
        if (_connection is null || !IsConnected) return LiveSendResult.NotConnected;
        try
        {
            return await _connection.InvokeAsync<LiveSendResult>(
                nameof(IBoardHubServer.SendTopicMessage), projectId, topic, kind, data);
        }
        catch (Exception e) when (e is HubException || Dropped(e))
        {
            return IsConnected ? LiveSendResult.Failed : LiveSendResult.NotConnected;
        }
    }

    /// <summary>
    /// Joins the board's live group, or — while the connection is down or
    /// still coming back, as it is for a while after the device was locked —
    /// remembers to, and joins it the moment the connection is back
    /// (<see cref="RestoreConnectionAsync"/>). The page works without live
    /// updates meanwhile; it mustn't fail to open.
    /// </summary>
    public async Task JoinBoard(Guid boardId)
    {
        bool waiting;
        await _gate.WaitAsync();
        try
        {
            await TryEnsureStartedAsync();
            if (_boardId == boardId)
            {
                return;
            }

            var previous = _boardId;
            _boardId = boardId;
            _presence = null;
            if (IsConnected)
            {
                try
                {
                    if (previous is { } left)
                    {
                        await _connection!.InvokeAsync(nameof(IBoardHubServer.LeaveBoard), left);
                    }

                    await _connection!.InvokeAsync(nameof(IBoardHubServer.JoinBoard), boardId);
                }
                catch (HubException)
                {
                    _boardId = previous;
                    throw;
                }
                catch (Exception e) when (Dropped(e))
                {
                    // Dropped mid-call; the reconnect joins it.
                }
            }

            waiting = _connection?.State == HubConnectionState.Reconnecting;
            await Notify();
        }
        finally
        {
            _gate.Release();
        }

        // Someone opening a board wants it live now, not at the backoff's
        // next try; in the background, so the page doesn't wait on it.
        if (waiting)
        {
            _ = Wake();
        }
    }

    public async Task LeaveBoard(Guid boardId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is null || _boardId != boardId)
            {
                return;
            }

            // Clear presence before leaving so the remaining clients see the
            // avatar disappear rather than waiting for a disconnect timeout.
            await SetPresenceCoreAsync(EntityTypes.Board, boardId, PresenceMode.None);
            await _connection.InvokeAsync(nameof(IBoardHubServer.LeaveBoard), boardId);
            _boardId = null;
            _presence = null;
            await Notify();
        }
        catch (Exception e) when (e is HubException || Dropped(e))
        {
            // Navigating away from a dead connection is not worth a stack trace.
            _boardId = null;
            _presence = null;
            await Notify();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Report viewing/editing an entity. Opening an editor reports
    /// <see cref="PresenceMode.Editing"/>, which is what warns everyone else off
    /// before they start typing.
    /// </summary>
    public async Task SetPresence(string entityType, Guid entityId, PresenceMode mode)
    {
        await _gate.WaitAsync();
        try
        {
            await SetPresenceCoreAsync(entityType, entityId, mode);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SubscribeTopic(Guid projectId, string topic)
    {
        await _gate.WaitAsync();
        try
        {
            if (_topics.TryGetValue((projectId, topic), out var held))
            {
                _topics[(projectId, topic)] = held with { Holders = held.Holders + 1 };
                return;
            }

            // Held before the call, so a connection that comes up later still
            // subscribes it on reconnect — unless the server refused it outright.
            _topics[(projectId, topic)] = (1, PresenceMode.None);
            if (!await TryEnsureStartedAsync() || !IsConnected)
            {
                return;
            }

            try
            {
                await _connection!.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), projectId, topic);
            }
            catch (HubException)
            {
                _topics.Remove((projectId, topic));
                throw;
            }
            catch (Exception e) when (Dropped(e))
            {
                // Dropped mid-call; the reconnect subscribes it.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnsubscribeTopic(Guid projectId, string topic)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_topics.TryGetValue((projectId, topic), out var held))
            {
                return;
            }

            if (held.Holders > 1)
            {
                _topics[(projectId, topic)] = held with { Holders = held.Holders - 1 };
                return;
            }

            _topics.Remove((projectId, topic));
            if (_connection is { State: HubConnectionState.Connected })
            {
                await _connection.InvokeAsync(nameof(IBoardHubServer.UnsubscribeTopic), projectId, topic);
            }

            await Notify();
        }
        catch (Exception e) when (e is HubException || Dropped(e))
        {
            // Leaving over a dead connection: the server forgets it on disconnect anyway.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetTopicPresence(Guid projectId, string topic, PresenceMode mode)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_topics.TryGetValue((projectId, topic), out var held))
            {
                return;
            }

            _topics[(projectId, topic)] = held with { Mode = mode };
            if (_connection is { State: HubConnectionState.Connected })
            {
                try
                {
                    await _connection.InvokeAsync(nameof(IBoardHubServer.SetTopicPresence), projectId, topic, mode);
                }
                catch (Exception e) when (Dropped(e))
                {
                    // The reconnect restates it.
                }
            }

            await Notify();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SubscribeUserTopic(string topic)
    {
        await _gate.WaitAsync();
        try
        {
            if (_userTopics.TryGetValue(topic, out var holders))
            {
                _userTopics[topic] = holders + 1;
                return;
            }

            _userTopics[topic] = 1;
            if (!await TryEnsureStartedAsync() || !IsConnected)
            {
                return;
            }

            try
            {
                await _connection!.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), topic);
            }
            catch (HubException)
            {
                _userTopics.Remove(topic);
                throw;
            }
            catch (Exception e) when (Dropped(e))
            {
                // Dropped mid-call; the reconnect subscribes it.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnsubscribeUserTopic(string topic)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_userTopics.TryGetValue(topic, out var holders))
            {
                return;
            }

            if (holders > 1)
            {
                _userTopics[topic] = holders - 1;
                return;
            }

            _userTopics.Remove(topic);
            if (_connection is { State: HubConnectionState.Connected })
            {
                await _connection.InvokeAsync(nameof(IBoardHubServer.UnsubscribeUserTopic), topic);
            }
        }
        catch (Exception e) when (e is HubException || Dropped(e))
        {
            // As with project topics: the server forgets it on disconnect anyway.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetPresenceCoreAsync(string entityType, Guid entityId, PresenceMode mode)
    {
        _presence = mode == PresenceMode.None ? null : (entityType, entityId, mode);
        if (_connection is not { State: HubConnectionState.Connected })
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync(nameof(IBoardHubServer.SetPresence), entityType, entityId, mode);
        }
        catch (Exception e) when (Dropped(e))
        {
            // Recorded above, so the reconnect restates it.
        }
    }

    /// <summary>
    /// What a call throws when the connection goes down under it: the
    /// browser's WebSocket error, a call made while it reconnects, one cut
    /// off by the close. None of them is the page's fault, and the reconnect
    /// restores whatever the call was for, so the callers swallow them rather
    /// than fail the page — after the device was locked, they did.
    /// </summary>
    private static bool Dropped(Exception e) =>
        e is WebSocketException or InvalidOperationException or HttpRequestException or IOException or OperationCanceledException;

    /// <summary>
    /// As <see cref="EnsureStartedAsync"/>, but a connection that can't start
    /// now is left to the retry rather than thrown: what the caller wanted is
    /// already recorded, and the reconnect applies it.
    /// </summary>
    private async Task<bool> TryEnsureStartedAsync()
    {
        try
        {
            await EnsureStartedAsync();
            return true;
        }
        catch (Exception e) when (e is ApiException || Dropped(e))
        {
            return false;
        }
    }

    /// <summary>
    /// The page came back into view, or the browser back online: reconnect now
    /// rather than at the next step of the automatic reconnect's backoff. After
    /// the device was locked, even for a few seconds, that backoff can have
    /// reached 10 or 30 seconds with every try failing while the app was
    /// suspended, and live updates would stay off that long after unlocking.
    /// </summary>
    [JSInvokable]
    public async Task Wake()
    {
        if (_disposed || _connection is null || _connection.State is HubConnectionState.Connected or HubConnectionState.Connecting)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_connection.State == HubConnectionState.Reconnecting)
            {
                // Ends the backoff; Closed fires and arms the retry timer,
                // which a successful start below disposes.
                await _connection.StopAsync();
            }

            await StartCoreAsync();
            _retry?.Dispose();
            _retry = null;
        }
        catch (Exception e) when (e is ApiException or HubException || Dropped(e))
        {
            ScheduleRetry();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync()
    {
        try { await StartCoreAsync(); }
        catch
        {
            ScheduleRetry();
            throw;
        }
    }

    // Every five seconds until a start succeeds.
    private void ScheduleRetry()
    {
        if (_disposed) return;
        _retry ??= new Timer(async _ => await RetryAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task RetryAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            await StartCoreAsync();
            _retry?.Dispose();
            _retry = null;
        }
        catch (Exception e) when (e is ApiException or HubException || Dropped(e)) { }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync()
    {
        if (_disposed) return;
        if (offline is not null && await offline.IsOffline() && !await offline.Reconnect()) throw new HttpRequestException("The live connection is offline.");
        if (_connection is not null)
        {
            if (_connection.State == HubConnectionState.Disconnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _connection.StartAsync(timeout.Token);
                await RestoreConnectionAsync(_connection);
            }

            return;
        }

        var connection = new HubConnectionBuilder()
            .WithUrl(instance.HubUri, options =>
                // Asked again for every start and reconnect. In a browser the
                // WebSocket's credential has to go in the URL, which proxies
                // log, so it is a one-use ticket and never the sign-in itself.
                options.AccessTokenProvider = async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    return await api.GetHubTicketAsync(timeout.Token);
                })
            .WithAutomaticReconnect()
            .Build();

        connection.On<EntityChangedEvent>(
            nameof(IBoardClient.EntityChanged),
            change => EntityChanged?.Invoke(change) ?? Task.CompletedTask);

        connection.On<BoardPresenceSnapshot>(
            nameof(IBoardClient.PresenceChanged),
            snapshot => PresenceChanged?.Invoke(snapshot) ?? Task.CompletedTask);

        connection.On<TopicMessage>(
            nameof(IBoardClient.TopicMessage),
            message => TopicMessage?.Invoke(message) ?? Task.CompletedTask);

        connection.On<TopicPresenceSnapshot>(
            nameof(IBoardClient.TopicPresenceChanged),
            snapshot => TopicPresenceChanged?.Invoke(snapshot) ?? Task.CompletedTask);

        connection.Reconnecting += _ => Notify();
        // Automatic reconnect gives up after four tries (0, 2, 10 and 30
        // seconds), which can all fail while the device is locked or the
        // network is still coming back. Closed is where it gives up, so keep
        // trying from there rather than stay disconnected until a reload.
        connection.Closed += _ =>
        {
            ScheduleRetry();
            return Notify();
        };
        connection.Reconnected += _ => RestoreConnectionAsync(connection);

        _connection = connection;
        await WatchWakeAsync();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.StartAsync(startTimeout.Token);
        await RestoreConnectionAsync(connection);
    }

    private async Task RestoreConnectionAsync(HubConnection connection)
    {
        // A reconnect lands in a new connection id and empty groups: rejoin
        // the board and the topics and restate presence, then let the page refetch.
        connections.ConnectionId = connection.ConnectionId;
        if (_boardId is { } boardId)
        {
            await connection.InvokeAsync(nameof(IBoardHubServer.JoinBoard), boardId);
            if (_presence is { } presence)
            {
                await connection.InvokeAsync(
                    nameof(IBoardHubServer.SetPresence),
                    presence.EntityType, presence.EntityId, presence.Mode);
            }
        }

        foreach (var ((projectId, topic), held) in _topics.ToList())
        {
            try
            {
                await connection.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), projectId, topic);
                if (held.Mode != PresenceMode.None)
                {
                    await connection.InvokeAsync(
                        nameof(IBoardHubServer.SetTopicPresence), projectId, topic, held.Mode);
                }
            }
            catch (HubException)
            {
                // Access lost while away, say: that topic stays quiet, the rest carry on.
                _topics.Remove((projectId, topic));
            }
        }

        foreach (var topic in _userTopics.Keys.ToList())
        {
            try
            {
                await connection.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), topic);
            }
            catch (HubException)
            {
                _userTopics.Remove(topic);
            }
        }

        await Notify();
    }

    private Task Notify() => ConnectionStateChanged?.Invoke() ?? Task.CompletedTask;

    // Once per client: the page's visibility and online events call Wake.
    private async Task WatchWakeAsync()
    {
        if (js is null || _self is not null) return;
        _self = DotNetObjectReference.Create(this);
        try
        {
            await js.InvokeVoidAsync("solo.live.watchWake", _self);
        }
        catch (JSException)
        {
            // Without it the automatic reconnect still gets there, later.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _retry?.Dispose();
        _self?.Dispose();
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null) await _connection.DisposeAsync();
            connections.ConnectionId = null;
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
