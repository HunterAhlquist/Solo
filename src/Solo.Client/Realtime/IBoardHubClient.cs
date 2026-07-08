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

    /// <summary>The live connection came or went, so the UI can show a "reconnecting" hint.</summary>
    event Func<Task>? ConnectionStateChanged;

    bool IsConnected { get; }
}
