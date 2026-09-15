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

/// <summary>Typed SignalR client contract for <c>BoardHub</c>.</summary>
public interface IBoardClient
{
    Task EntityChanged(EntityChangedEvent change);

    Task PresenceChanged(BoardPresenceSnapshot snapshot);
}

/// <summary>Server methods a client can invoke on <c>BoardHub</c>.</summary>
public interface IBoardHubServer
{
    Task JoinBoard(Guid boardId);

    Task LeaveBoard(Guid boardId);

    /// <summary>Report viewing/editing an entity; <see cref="PresenceMode.None"/> clears it.</summary>
    Task SetPresence(string entityType, Guid entityId, PresenceMode mode);
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
}
