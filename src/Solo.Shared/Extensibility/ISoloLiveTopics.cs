using Solo.Shared.Hubs;

namespace Solo.Shared.Extensibility;

/// <summary>
/// An extension's live topics, over the host's own realtime connection: the
/// messages its server half publishes through <c>ISoloLiveUpdates</c>, and who
/// else is on the same page. Injected like <see cref="ISoloClientApi"/>.
/// </summary>
/// <remarks>
/// The connection is shared with the board, and survives reconnects: held
/// topics and reported presence are restated on the new connection. A message
/// is a nudge — refetch through the API on receipt rather than trusting its
/// data as state. Subscribing needs read access to the project.
/// </remarks>
public interface ISoloLiveTopics
{
    /// <summary>
    /// Starts receiving a project's topic. <paramref name="onPresence"/>, when
    /// given, hears who else is present there — the signed-in user is left out,
    /// as the board's own presence leaves them out. Dispose the result to stop;
    /// it also clears any presence reported on the topic.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(
        Guid projectId,
        string topic,
        Func<TopicMessage, Task> onMessage,
        Func<IReadOnlyList<TopicPresenceEntry>, Task>? onPresence = null);

    /// <summary>
    /// Reports the signed-in user on a subscribed topic: viewing, editing, or
    /// (<see cref="PresenceMode.None"/>) gone.
    /// </summary>
    Task SetPresenceAsync(Guid projectId, string topic, PresenceMode mode);

    /// <summary>
    /// Starts receiving the signed-in user's own topic: what the server half
    /// publishes with <c>ISoloLiveUpdates.PublishToUserAsync</c> for this person,
    /// in every tab they have open (contract 1.8). Messages carry an empty
    /// <c>ProjectId</c>. Dispose the result to stop.
    /// </summary>
    /// <remarks>
    /// The hub joins the topic for whoever is signed in on the connection, so
    /// there is nothing to check and no way to ask for someone else's. There is
    /// no presence on a user topic: the only person on it is the one listening.
    /// </remarks>
    Task<IAsyncDisposable> SubscribeUserAsync(string topic, Func<TopicMessage, Task> onMessage);
}
