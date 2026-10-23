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

    /// <summary>Report viewing/editing a subscribed topic; <see cref="PresenceMode.None"/> clears it.</summary>
    Task SetTopicPresence(Guid projectId, string topic, PresenceMode mode);

    /// <summary>
    /// Start receiving the signed-in user's own extension topic. Nothing to
    /// check: the group is the connection's own user's, so no caller can name
    /// anyone else's. Counts toward <see cref="BoardHubConstants.TopicsPerConnection"/>.
    /// </summary>
    Task SubscribeUserTopic(string topic);

    Task UnsubscribeUserTopic(string topic);

    /// <summary>
    /// Send a message on a subscribed project topic, for the extension that owns
    /// the topic to drop, answer or relay (contract 1.10). Holding the topic is
    /// the access check. Refusals come back as a result rather than an error, so
    /// a client that must know whether something landed can resend it.
    /// </summary>
    Task<LiveSendResult> SendTopicMessage(Guid projectId, string topic, string kind, string? data);
}

/// <summary>What became of a client-sent topic message.</summary>
public enum LiveSendResult
{
    /// <summary>The owning extension's handler ran to completion.</summary>
    Accepted = 0,

    /// <summary>This connection doesn't hold the topic, or holds a user topic.</summary>
    NotSubscribed = 1,

    /// <summary>The topic's extension registered no handler, or is disabled.</summary>
    NoHandler = 2,

    /// <summary>The kind or the data is longer than allowed; see <see cref="BoardHubConstants.ClientMessageMaxLength"/>.</summary>
    TooLarge = 3,

    /// <summary>The connection sent faster than its live allowance. Back off and resend.</summary>
    RateLimited = 4,

    /// <summary>The handler threw or ran out of time.</summary>
    Failed = 5,

    /// <summary>Client side only: there was no live connection to send it over.</summary>
    NotConnected = 6,
}

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

    /// <summary>Topics one connection may hold at once — a page and its sidebar need a handful.</summary>
    public const int TopicsPerConnection = 64;

    /// <summary>Longest kind a client may send on a topic.</summary>
    public const int ClientMessageKindMaxLength = 64;

    /// <summary>
    /// Longest data a client may send in one topic message, in characters. Sized
    /// so a message plus its JSON envelope fits the hub's 64 KB receive limit;
    /// anything bigger is the sender's to split.
    /// </summary>
    public const int ClientMessageMaxLength = 48_000;
}
