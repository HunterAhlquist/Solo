using Solo.Shared.Hubs;

namespace Solo.Client.Realtime;

/// <summary>
/// The board hub as the rest of the client sees it: the shared
/// <see cref="IBoardHubServer"/> contract plus the <see cref="IBoardClient"/>
/// callbacks surfaced as events. Behind an interface so the store and components
/// can be tested without opening a WebSocket.
/// </summary>
public interface IBoardHubClient : IBoardHubServer
{
    /// <summary>A change landed from another client. Field names only — refetch to get values.</summary>
    event Func<EntityChangedEvent, Task>? EntityChanged;

    /// <summary>Full presence snapshot for the joined board.</summary>
    event Func<BoardPresenceSnapshot, Task>? PresenceChanged;

    /// <summary>A message on a subscribed extension topic.</summary>
    event Func<TopicMessage, Task>? TopicMessage;

    /// <summary>Who is present on a subscribed extension topic.</summary>
    event Func<TopicPresenceSnapshot, Task>? TopicPresenceChanged;

    /// <summary>The live connection came or went, so the UI can show a "reconnecting" hint.</summary>
    event Func<Task>? ConnectionStateChanged;

    bool IsConnected { get; }

    /// <summary>
    /// True while attached to a board, or present on an extension's topic — i.e.
    /// a page that is live expects a connection. Off those (project lists,
    /// settings) there is nothing to connect to, so the "reconnecting" hint should
    /// stay hidden even though <see cref="IsConnected"/> is false.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Restarts a connection that stopped trying to reconnect, restoring what it
    /// held. Does nothing while it is up or still retrying on its own.
    /// </summary>
    Task EnsureConnectedAsync();
}
