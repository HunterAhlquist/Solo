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

    /// <summary>
    /// Sends a message on a subscribed project topic, for its extension's server
    /// handler (<c>ISoloLiveMessageHandler</c>) to drop, answer or relay
    /// (contract 1.10). Answers come back to the subscription's
    /// <c>onMessage</c> like anything the server publishes.
    /// </summary>
    /// <remarks>
    /// <para>Never throws for a refusal or a missing connection; the result says
    /// what happened, so something that must land can be resent.
    /// <see cref="LiveSendResult.Accepted"/> means the handler ran, not that it
    /// agreed: a handler answers in its own words.</para>
    ///
    /// <para>While the connection is down this returns
    /// <see cref="LiveSendResult.NotConnected"/>, and asks a connection that
    /// gave up reconnecting to try again. <see cref="ConnectionChanged"/> says
    /// when it's back.</para>
    /// </remarks>
    Task<LiveSendResult> SendAsync(Guid projectId, string topic, string kind, string? data = null,
        CancellationToken ct = default);

    /// <summary>True while the live connection is up (contract 1.10).</summary>
    bool IsConnected { get; }

    /// <summary>
    /// The connection went down, or came back with held topics already
    /// resubscribed (contract 1.10). After a gap, refetch or resync: anything
    /// published or relayed in it was missed. Raised only when
    /// <see cref="IsConnected"/> actually changes.
    /// </summary>
    event Func<Task>? ConnectionChanged;
}
