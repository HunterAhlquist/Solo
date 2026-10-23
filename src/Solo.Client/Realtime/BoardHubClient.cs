using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
    ConnectionIdAccessor connections)
    : IBoardHubClient, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
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

    public async Task JoinBoard(Guid boardId)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureStartedAsync();
            if (_boardId == boardId)
            {
                return;
            }

            if (_boardId is { } previous)
            {
                await _connection!.InvokeAsync(nameof(IBoardHubServer.LeaveBoard), previous);
            }

            await _connection!.InvokeAsync(nameof(IBoardHubServer.JoinBoard), boardId);
            _boardId = boardId;
            _presence = null;
            await Notify();
        }
        finally
        {
            _gate.Release();
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
        catch (Exception e) when (e is HubException or InvalidOperationException)
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
            await EnsureStartedAsync();
            try
            {
                await _connection!.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), projectId, topic);
            }
            catch (HubException)
            {
                _topics.Remove((projectId, topic));
                throw;
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
        catch (Exception e) when (e is HubException or InvalidOperationException)
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
                await _connection.InvokeAsync(nameof(IBoardHubServer.SetTopicPresence), projectId, topic, mode);
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
            await EnsureStartedAsync();
            try
            {
                await _connection!.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), topic);
            }
            catch (HubException)
            {
                _userTopics.Remove(topic);
                throw;
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
        catch (Exception e) when (e is HubException or InvalidOperationException)
        {
            // As with project topics: the server forgets it on disconnect anyway.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends outside the gate: a document being typed into sends every few tens
    /// of milliseconds, and must not queue behind a subscribe that is waiting
    /// out a reconnect. Over a connection that isn't up it sends nothing.
    /// </summary>
    public async Task<LiveSendResult> SendTopicMessage(Guid projectId, string topic, string kind, string? data)
    {
        if (_connection is not { State: HubConnectionState.Connected } connection)
        {
            return LiveSendResult.NotConnected;
        }

        try
        {
            return await connection.InvokeAsync<LiveSendResult>(
                nameof(IBoardHubServer.SendTopicMessage), projectId, topic, kind, data);
        }
        catch (Exception e) when (e is InvalidOperationException or TaskCanceledException or IOException)
        {
            // The connection dropped mid-send. The caller can't know whether it
            // landed, which is the same as not having sent it.
            return LiveSendResult.NotConnected;
        }
    }

    /// <summary>
    /// Brings back a connection that gave up reconnecting, with everything it
    /// held. Automatic reconnects stop after a few tries, and a tablet that was
    /// asleep for longer comes back to a closed connection.
    /// </summary>
    public async Task EnsureConnectedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is { State: HubConnectionState.Disconnected })
            {
                await EnsureStartedAsync();
            }
        }
        catch (Exception e) when (e is HubException or InvalidOperationException or HttpRequestException)
        {
            // Still offline: the next try will come.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetPresenceCoreAsync(string entityType, Guid entityId, PresenceMode mode)
    {
        if (_connection is not { State: HubConnectionState.Connected })
        {
            return;
        }

        await _connection.InvokeAsync(nameof(IBoardHubServer.SetPresence), entityType, entityId, mode);
        _presence = mode == PresenceMode.None ? null : (entityType, entityId, mode);
    }

    private async Task EnsureStartedAsync()
    {
        if (_connection is not null)
        {
            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync();
                connections.ConnectionId = _connection.ConnectionId;
                // A fresh start is a new connection with empty groups, the same
                // as after an automatic reconnect.
                await RestoreAsync(_connection);
            }

            return;
        }

        var connection = new HubConnectionBuilder()
            .WithUrl(instance.HubUri, options =>
                // Asked again for every start and reconnect. In a browser the
                // WebSocket's credential has to go in the URL, which proxies
                // log, so it is a one-use ticket and never the sign-in itself.
                options.AccessTokenProvider = async () => await api.GetHubTicketAsync())
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
        connection.Closed += _ => Notify();
        connection.Reconnected += async _ =>
        {
            connections.ConnectionId = connection.ConnectionId;
            await RestoreAsync(connection);
        };

        _connection = connection;
        await connection.StartAsync();
        connections.ConnectionId = connection.ConnectionId;
        // Whatever was held before the first start (the topic being subscribed
        // now, typically) is subscribed before anyone hears the connection is up,
        // so a page that sends on hearing it isn't refused as not subscribed.
        await RestoreAsync(connection);
    }

    /// <summary>
    /// A new connection id comes with empty groups: rejoin the board and the
    /// topics and restate presence, then let the page refetch.
    /// </summary>
    private async Task RestoreAsync(HubConnection connection)
    {
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

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        connections.ConnectionId = null;
        _gate.Dispose();
    }
}
