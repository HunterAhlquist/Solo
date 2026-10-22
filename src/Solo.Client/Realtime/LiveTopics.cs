using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.Realtime;

/// <summary>
/// The host's side of <see cref="ISoloLiveTopics"/>: hands the board hub's topic
/// traffic to whichever extension components asked for it.
/// </summary>
/// <remarks>
/// Best-effort like the rest of the live connection. A topic the server refuses,
/// or a connection that can't come up, leaves the subscriber quiet rather than
/// throwing into an extension's page — the page still works by reloading.
/// </remarks>
public sealed class LiveTopics : ISoloLiveTopics, IDisposable
{
    private sealed record Subscriber(
        Guid ProjectId,
        string Topic,
        Func<TopicMessage, Task> OnMessage,
        Func<IReadOnlyList<TopicPresenceEntry>, Task>? OnPresence);

    private bool _lastConnected;
    private readonly IBoardHubClient _hub;
    private readonly UserSession _session;
    private readonly ILogger<LiveTopics> _logger;
    private readonly List<Subscriber> _subscribers = [];
    private readonly Dictionary<(Guid, string), TopicPresenceSnapshot> _presence = [];
    private readonly HashSet<(Guid, string)> _reporting = [];

    public LiveTopics(IBoardHubClient hub, UserSession session, ILogger<LiveTopics> logger)
    {
        _hub = hub;
        _lastConnected = hub.IsConnected;
        _session = session;
        _logger = logger;
        _hub.TopicMessage += OnMessageAsync;
        _hub.ConnectionStateChanged += OnConnectionChangedAsync;
        _hub.TopicPresenceChanged += OnPresenceAsync;
    }

    public bool IsConnected => _hub.IsConnected;
    public event Func<Task>? ConnectionChanged;

    public async Task<LiveSendResult> SendAsync(Guid projectId, string topic, string kind, string? data = null,
        CancellationToken ct = default)
    {
        if (!IsConnected) return LiveSendResult.NotConnected;
        return await _hub.SendTopicMessage(projectId, topic, kind, data).WaitAsync(ct);
    }

    private async Task OnConnectionChangedAsync()
    {
        // The hub also notifies its UI after presence and subscription changes.
        // Only a transport transition means an extension needs to resynchronize.
        var connected = _hub.IsConnected;
        if (connected == _lastConnected) return;
        _lastConnected = connected;
        if (ConnectionChanged is { } changed)
            foreach (Func<Task> handler in changed.GetInvocationList()) await DeliverAsync(handler);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        Guid projectId,
        string topic,
        Func<TopicMessage, Task> onMessage,
        Func<IReadOnlyList<TopicPresenceEntry>, Task>? onPresence = null)
    {
        var subscriber = new Subscriber(projectId, topic, onMessage, onPresence);
        _subscribers.Add(subscriber);
        try
        {
            await _hub.SubscribeTopic(projectId, topic);
        }
        catch (Exception e) when (e is HubException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogWarning(e, "Couldn't subscribe to live topic {Topic}; it will stay quiet.", topic);
        }

        // A second subscriber to a topic already held hears nothing from the
        // server on joining, so it gets the last snapshot from here.
        if (onPresence is not null && _presence.TryGetValue((projectId, topic), out var snapshot))
        {
            await DeliverAsync(() => onPresence(Others(snapshot)));
        }

        return new Subscription(this, subscriber);
    }

    public async Task<IAsyncDisposable> SubscribeUserAsync(string topic, Func<TopicMessage, Task> onMessage)
    {
        // Held under an empty project id, which is what the server's messages on
        // a user topic carry, so the one dispatch path serves both kinds.
        var subscriber = new Subscriber(Guid.Empty, topic, onMessage, null);
        _subscribers.Add(subscriber);
        try
        {
            await _hub.SubscribeUserTopic(topic);
        }
        catch (Exception e) when (e is HubException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogWarning(e, "Couldn't subscribe to user topic {Topic}; it will stay quiet.", topic);
        }

        return new Subscription(this, subscriber);
    }

    public async Task SetPresenceAsync(Guid projectId, string topic, PresenceMode mode)
    {
        if (mode == PresenceMode.None)
        {
            _reporting.Remove((projectId, topic));
        }
        else
        {
            _reporting.Add((projectId, topic));
        }

        try
        {
            await _hub.SetTopicPresence(projectId, topic, mode);
        }
        catch (Exception e) when (e is HubException or InvalidOperationException)
        {
            _logger.LogWarning(e, "Couldn't report presence on live topic {Topic}.", topic);
        }
    }

    private async Task UnsubscribeAsync(Subscriber subscriber)
    {
        if (!_subscribers.Remove(subscriber))
        {
            return;
        }

        if (subscriber.ProjectId == Guid.Empty)
        {
            await _hub.UnsubscribeUserTopic(subscriber.Topic);
            return;
        }

        var key = (subscriber.ProjectId, subscriber.Topic);
        var stillHeld = _subscribers.Any(s => (s.ProjectId, s.Topic) == key);
        if (stillHeld && _reporting.Contains(key))
        {
            // Another component still listens here; only the presence goes.
            await SetPresenceAsync(subscriber.ProjectId, subscriber.Topic, PresenceMode.None);
        }

        if (!stillHeld)
        {
            _reporting.Remove(key);
            _presence.Remove(key);
        }

        // Counted by the hub client: the server subscription goes with the last holder.
        await _hub.UnsubscribeTopic(subscriber.ProjectId, subscriber.Topic);
    }

    private async Task OnMessageAsync(TopicMessage message)
    {
        foreach (var subscriber in Matching(message.ProjectId, message.Topic))
        {
            await DeliverAsync(() => subscriber.OnMessage(message));
        }
    }

    private async Task OnPresenceAsync(TopicPresenceSnapshot snapshot)
    {
        _presence[(snapshot.ProjectId, snapshot.Topic)] = snapshot;
        var others = Others(snapshot);
        foreach (var subscriber in Matching(snapshot.ProjectId, snapshot.Topic))
        {
            if (subscriber.OnPresence is { } onPresence)
            {
                await DeliverAsync(() => onPresence(others));
            }
        }
    }

    private List<Subscriber> Matching(Guid projectId, string topic) =>
        _subscribers.Where(s => s.ProjectId == projectId && s.Topic == topic).ToList();

    private IReadOnlyList<TopicPresenceEntry> Others(TopicPresenceSnapshot snapshot) =>
        snapshot.Entries.Where(e => e.UserId != _session.UserId).ToList();

    /// <summary>One extension's handler failing mustn't stop the next from hearing it.</summary>
    private async Task DeliverAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "A live topic handler failed.");
        }
    }

    public void Dispose()
    {
        _hub.TopicMessage -= OnMessageAsync;
        _hub.ConnectionStateChanged -= OnConnectionChangedAsync;
        _hub.TopicPresenceChanged -= OnPresenceAsync;
    }

    private sealed class Subscription(LiveTopics owner, Subscriber subscriber) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(owner.UnsubscribeAsync(subscriber));
    }
}
