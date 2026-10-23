using Microsoft.AspNetCore.SignalR;
using Solo.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// A handler's answer to one client-sent message, as ordinary topic messages
/// carrying the sender as their actor, so the client's dispatch is the same as
/// for anything the server publishes.
/// </summary>
internal sealed class LiveMessageReply(
    IHubContext<BoardHub, IBoardClient> hub, string connectionId, Guid projectId, string topic, Guid senderId)
    : ILiveMessageReply
{
    public Task ToSenderAsync(string kind, string? data = null, CancellationToken ct = default) =>
        hub.Clients.Client(connectionId).TopicMessage(Message(kind, data));

    public Task ToOthersAsync(string kind, string? data = null, CancellationToken ct = default) =>
        hub.Clients.GroupExcept(BoardHubConstants.TopicGroup(projectId, topic), [connectionId])
            .TopicMessage(Message(kind, data));

    private TopicMessage Message(string kind, string? data) => new(projectId, topic, kind, data, senderId);
}
