namespace Solo.Shared.Hubs;

/// <summary>
/// Broadcast after a successful save: clients patch local state.
/// <paramref name="EntityType"/> is one of the <see cref="EntityTypes"/> constants.
/// </summary>
public sealed record EntityChangedEvent(
    string EntityType,
    Guid EntityId,
    ChangeKind Kind,
    string[] ChangedFields,
    Guid ActorUserId);

/// <summary>One user's presence on an entity within a board.</summary>
public sealed record PresenceEntry(
    Guid UserId,
    string? DisplayName,
    string EntityType,
    Guid EntityId,
    PresenceMode Mode);

/// <summary>Full presence snapshot for a board, broadcast on every presence change.</summary>
public sealed record BoardPresenceSnapshot(
    Guid BoardId,
    IReadOnlyList<PresenceEntry> Entries);

/// <summary>
/// A message an extension published on one of its live topics in a project.
/// <paramref name="Kind"/> and <paramref name="Data"/> mean whatever the
/// extension says they mean; core only routes them.
/// </summary>
public sealed record TopicMessage(Guid ProjectId, string Topic, string Kind, string? Data, Guid ActorUserId);

/// <summary>One user's presence on a topic.</summary>
public sealed record TopicPresenceEntry(Guid UserId, string? DisplayName, PresenceMode Mode);

/// <summary>Everyone present on a topic, broadcast to its subscribers on every change.</summary>
public sealed record TopicPresenceSnapshot(Guid ProjectId, string Topic, IReadOnlyList<TopicPresenceEntry> Entries);

/// <summary>Typed SignalR client contract for <c>BoardHub</c>.</summary>
public interface IBoardClient
{
    Task EntityChanged(EntityChangedEvent change);

    Task PresenceChanged(BoardPresenceSnapshot snapshot);

    Task TopicMessage(TopicMessage message);

    Task TopicPresenceChanged(TopicPresenceSnapshot snapshot);
}

/// <summary>Server methods a client can invoke on <c>BoardHub</c>.</summary>
public interface IBoardHubServer
{
    Task JoinBoard(Guid boardId);

    Task LeaveBoard(Guid boardId);

    /// <summary>Report viewing/editing an entity; <see cref="PresenceMode.None"/> clears it.</summary>
    Task SetPresence(string entityType, Guid entityId, PresenceMode mode);

    /// <summary>
    /// Start receiving an extension topic's messages and presence. Needs read
    /// access to the project, checked here the way <see cref="JoinBoard"/> checks it.
    /// </summary>
    Task SubscribeTopic(Guid projectId, string topic);

    Task UnsubscribeTopic(Guid projectId, string topic);

    /// <summary>Sends to the owner of a held project topic (contract 1.10).</summary>
    Task<LiveSendResult> SendTopicMessage(Guid projectId, string topic, string kind, string? data);

    /// <summary>Report viewing/editing a subscribed topic; <see cref="PresenceMode.None"/> clears it.</summary>
    Task SetTopicPresence(Guid projectId, string topic, PresenceMode mode);

    /// <summary>
    /// Start receiving the signed-in user's own extension topic. Nothing to
    /// check: the group is the connection's own user's, so no caller can name
    /// anyone else's. Counts toward <see cref="BoardHubConstants.TopicsPerConnection"/>.
    /// </summary>
    Task SubscribeUserTopic(string topic);

    Task UnsubscribeUserTopic(string topic);
}

/// <summary>Whether a client message reached its handler; Accepted does not imply a persisted edit.</summary>
public enum LiveSendResult { Accepted, NotSubscribed, NoHandler, TooLarge, RateLimited, Failed, NotConnected }

/// <summary>Entity type names used in change events and presence reports.</summary>
public static class EntityTypes
{
    public const string Project = "project";
    public const string Board = "board";
    public const string EpicGroup = "epicGroup";
    public const string Epic = "epic";
    public const string Task = "task";
    public const string SubTask = "subtask";
    public const string Label = "label";
    public const string Comment = "comment";
}

/// <summary>Well-known SignalR endpoints/headers shared with the client.</summary>
public static class BoardHubConstants
{
    public const string Path = "/hubs/board";

    /// <summary>
    /// REST callers include their hub connection id in this header so change
    /// broadcasts can exclude the sender.
    /// </summary>
    public const string ConnectionIdHeader = "X-Connection-Id";

    public static string BoardGroup(Guid boardId) => $"board:{boardId}";

    public static string TopicGroup(Guid projectId, string topic) => $"topic:{projectId:N}:{topic}";

    /// <summary>One user's own topic. A different prefix from <see cref="TopicGroup"/>, so no project id can collide with a user id.</summary>
    public static string UserTopicGroup(Guid userId, string topic) => $"usertopic:{userId:N}:{topic}";

    /// <summary>Longest topic name accepted; topics are short keys like "roadmap:{id}", not payloads.</summary>
    public const int TopicMaxLength = 200;

    public const int ClientMessageMaxLength = 48_000;

    /// <summary>Topics one connection may hold at once — a page and its sidebar need a handful.</summary>
    public const int TopicsPerConnection = 64;
}
