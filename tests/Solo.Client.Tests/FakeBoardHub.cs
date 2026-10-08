using Solo.Client.Realtime;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>Drives the change feed and presence without opening a WebSocket.</summary>
internal sealed class FakeBoardHub : IBoardHubClient
{
    public event Func<EntityChangedEvent, Task>? EntityChanged;

    public event Func<BoardPresenceSnapshot, Task>? PresenceChanged;

    public event Func<TopicMessage, Task>? TopicMessage;

    public event Func<TopicPresenceSnapshot, Task>? TopicPresenceChanged;

    public event Func<Task>? ConnectionStateChanged;

    public bool IsConnected { get; set; } = true;

    public bool IsActive { get; set; }

    public List<Guid> Joined { get; } = [];

    public List<Guid> Left { get; } = [];

    public List<(string EntityType, Guid EntityId, PresenceMode Mode)> PresenceReports { get; } = [];

    public Task JoinBoard(Guid boardId)
    {
        Joined.Add(boardId);
        return Task.CompletedTask;
    }

    public Task LeaveBoard(Guid boardId)
    {
        Left.Add(boardId);
        return Task.CompletedTask;
    }

    public Task SetPresence(string entityType, Guid entityId, PresenceMode mode)
    {
        PresenceReports.Add((entityType, entityId, mode));
        return Task.CompletedTask;
    }

    public List<(Guid ProjectId, string Topic)> Subscribed { get; } = [];

    public List<(Guid ProjectId, string Topic)> Unsubscribed { get; } = [];

    public List<(Guid ProjectId, string Topic, PresenceMode Mode)> TopicPresenceReports { get; } = [];

    public Task SubscribeTopic(Guid projectId, string topic)
    {
        Subscribed.Add((projectId, topic));
        return Task.CompletedTask;
    }

    public Task UnsubscribeTopic(Guid projectId, string topic)
    {
        Unsubscribed.Add((projectId, topic));
        return Task.CompletedTask;
    }

    public Task SetTopicPresence(Guid projectId, string topic, PresenceMode mode)
    {
        TopicPresenceReports.Add((projectId, topic, mode));
        return Task.CompletedTask;
    }

    public List<string> UserSubscribed { get; } = [];

    public List<string> UserUnsubscribed { get; } = [];

    public Task SubscribeUserTopic(string topic)
    {
        UserSubscribed.Add(topic);
        return Task.CompletedTask;
    }

    public Task UnsubscribeUserTopic(string topic)
    {
        UserUnsubscribed.Add(topic);
        return Task.CompletedTask;
    }

    public Task RaiseTopicMessage(TopicMessage message) =>
        TopicMessage?.Invoke(message) ?? Task.CompletedTask;

    public Task RaiseTopicPresence(TopicPresenceSnapshot snapshot) =>
        TopicPresenceChanged?.Invoke(snapshot) ?? Task.CompletedTask;

    public Task RaiseEntityChanged(EntityChangedEvent change) =>
        EntityChanged?.Invoke(change) ?? Task.CompletedTask;

    public Task RaisePresence(BoardPresenceSnapshot snapshot) =>
        PresenceChanged?.Invoke(snapshot) ?? Task.CompletedTask;

    public Task RaiseConnectionStateChanged() => ConnectionStateChanged?.Invoke() ?? Task.CompletedTask;
}
