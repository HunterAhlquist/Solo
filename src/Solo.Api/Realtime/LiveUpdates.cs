using Microsoft.AspNetCore.SignalR;
using Solo.Api.Auth;
using Solo.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>Extension topics over the board hub, sender excluded the way <see cref="BoardNotifier"/> does it.</summary>
public sealed class LiveUpdates(
    IHubContext<BoardHub, IBoardClient> hub,
    CurrentUserAccessor currentUser,
    IHttpContextAccessor httpContextAccessor)
    : ISoloLiveUpdates
{
    public Task PublishAsync(Guid projectId, string topic, string kind, string? data = null,
        CancellationToken ct = default)
    {
        var group = BoardHubConstants.TopicGroup(projectId, topic);
        var sender = httpContextAccessor.HttpContext?.Request.Headers[BoardHubConstants.ConnectionIdHeader]
            .FirstOrDefault();
        var target = string.IsNullOrEmpty(sender)
            ? hub.Clients.Group(group)
            : hub.Clients.GroupExcept(group, [sender]);
        return target.TopicMessage(new TopicMessage(projectId, topic, kind, data, currentUser.UserId ?? Guid.Empty));
    }

    public Task PublishToUserAsync(Guid userId, string topic, string kind, string? data = null,
        CancellationToken ct = default)
    {
        var group = BoardHubConstants.UserTopicGroup(userId, topic);
        var sender = httpContextAccessor.HttpContext?.Request.Headers[BoardHubConstants.ConnectionIdHeader]
            .FirstOrDefault();
        var target = string.IsNullOrEmpty(sender)
            ? hub.Clients.Group(group)
            : hub.Clients.GroupExcept(group, [sender]);
        // No project: an empty id is how the client tells a user topic's message apart.
        return target.TopicMessage(new TopicMessage(Guid.Empty, topic, kind, data, currentUser.UserId ?? Guid.Empty));
    }
}
