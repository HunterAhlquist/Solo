using Solo.Shared;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Roadmap.Tests;

/// <summary>
/// The host's live topics with no connection behind them: records what a
/// component held and reported, and lets a test speak for the server.
/// </summary>
internal sealed class FakeLiveTopics : ISoloLiveTopics
{
    private sealed record Holder(
        Guid ProjectId,
        string Topic,
        Func<TopicMessage, Task> OnMessage,
        Func<IReadOnlyList<TopicPresenceEntry>, Task>? OnPresence);

    private readonly List<Holder> _holders = [];

    public IReadOnlyList<(Guid ProjectId, string Topic)> Held => _holders.Select(h => (h.ProjectId, h.Topic)).ToList();

    public List<(string Topic, PresenceMode Mode)> Reported { get; } = [];

    public Task<IAsyncDisposable> SubscribeAsync(
        Guid projectId,
        string topic,
        Func<TopicMessage, Task> onMessage,
        Func<IReadOnlyList<TopicPresenceEntry>, Task>? onPresence = null)
    {
        var holder = new Holder(projectId, topic, onMessage, onPresence);
        _holders.Add(holder);
        return Task.FromResult<IAsyncDisposable>(new Release(() => _holders.Remove(holder)));
    }

    /// <summary>Roadmaps hold no user topics; held under an empty project, as the host does.</summary>
    public Task<IAsyncDisposable> SubscribeUserAsync(string topic, Func<TopicMessage, Task> onMessage) =>
        SubscribeAsync(Guid.Empty, topic, onMessage);

    public Task SetPresenceAsync(Guid projectId, string topic, PresenceMode mode)
    {
        Reported.Add((topic, mode));
        return Task.CompletedTask;
    }

    /// <summary>A message from someone else, as the server would relay it.</summary>
    public async Task PublishAsync(Guid projectId, string topic, string kind, string? data = null)
    {
        var message = new TopicMessage(projectId, topic, kind, data, TestData.OtherId);
        foreach (var holder in Matching(projectId, topic))
        {
            await holder.OnMessage(message);
        }
    }

    /// <summary>Who else is on the topic, the signed-in user already left out.</summary>
    public async Task PresentAsync(Guid projectId, string topic, params TopicPresenceEntry[] others)
    {
        foreach (var holder in Matching(projectId, topic))
        {
            if (holder.OnPresence is { } onPresence)
            {
                await onPresence(others);
            }
        }
    }

    /// <summary>Roadmaps sends nothing on its topics (contract 1.10 added sending); recorded in case it starts.</summary>
    public List<(Guid ProjectId, string Topic, string Kind, string? Data)> Sent { get; } = [];

    public Task<LiveSendResult> SendAsync(Guid projectId, string topic, string kind, string? data = null,
        CancellationToken ct = default)
    {
        Sent.Add((projectId, topic, kind, data));
        return Task.FromResult(LiveSendResult.NoHandler);
    }

    public bool IsConnected => true;

#pragma warning disable CS0067 // Part of the contract; nothing in these tests drops the connection.
    public event Func<Task>? ConnectionChanged;
#pragma warning restore CS0067

    private List<Holder> Matching(Guid projectId, string topic) =>
        _holders.Where(h => h.ProjectId == projectId && h.Topic == topic).ToList();

    private sealed class Release(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release();
            return ValueTask.CompletedTask;
        }
    }
}
