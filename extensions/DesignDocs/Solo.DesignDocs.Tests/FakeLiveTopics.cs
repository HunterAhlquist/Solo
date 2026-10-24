using Solo.Shared;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.DesignDocs.Tests;

internal sealed class FakeLiveTopics : ISoloLiveTopics
{
    public bool IsConnected => true;
    public event Func<Task>? ConnectionChanged { add { } remove { } }
    Task<LiveSendResult> ISoloLiveTopics.SendAsync(Guid projectId, string topic, string kind, string? data,
        CancellationToken ct) => Task.FromResult(LiveSendResult.Accepted);

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

    public Task<IAsyncDisposable> SubscribeUserAsync(string topic, Func<TopicMessage, Task> onMessage) =>
        SubscribeAsync(Guid.Empty, topic, onMessage);

    public Task SetPresenceAsync(Guid projectId, string topic, PresenceMode mode)
    {
        Reported.Add((topic, mode));
        return Task.CompletedTask;
    }

    public async Task SendAsync(Guid projectId, string topic, string kind, string? data = null)
    {
        var message = new TopicMessage(projectId, topic, kind, data, TestData.OtherId);
        foreach (var holder in Matching(projectId, topic))
        {
            await holder.OnMessage(message);
        }
    }

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
