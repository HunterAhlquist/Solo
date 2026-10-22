using Microsoft.AspNetCore.SignalR;
using Solo.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

internal sealed class LiveMessageReply(
    IHubContext<BoardHub, IBoardClient> hub, string connectionId, LiveMessage message,
    CancellationToken invocationToken) : ILiveMessageReply
{
    public Task ToSenderAsync(string kind, string? data = null, CancellationToken ct = default)
    {
        invocationToken.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        return hub.Clients.Client(connectionId).TopicMessage(Reply(kind, data));
    }

    public Task ToOthersAsync(string kind, string? data = null, CancellationToken ct = default)
    {
        invocationToken.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        return hub.Clients.GroupExcept(BoardHubConstants.TopicGroup(message.ProjectId, message.Topic), [connectionId])
            .TopicMessage(Reply(kind, data));
    }

    private TopicMessage Reply(string kind, string? data) =>
        new(message.ProjectId, message.Topic, kind, data, message.Sender.UserId);
}
