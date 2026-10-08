namespace Solo.Extensibility;

/// <summary>
/// Pushes a message to every client subscribed to one of a project's live
/// topics — the client half subscribes through <c>ISoloLiveTopics</c>. This is
/// how an extension's pages stay current while someone else edits: core's own
/// board broadcasts only cover core's own entities.
/// </summary>
/// <remarks>
/// <para>A topic is a short key the extension chooses, such as
/// <c>"roadmap:{id}"</c>; prefix it with something of your own so two
/// extensions can't collide. <c>kind</c> and <c>data</c> are yours to define,
/// and are best kept to "what changed" rather than the new state: a
/// subscriber refetches through the API, which applies its access checks.
/// Publishing doesn't check access itself — only members of the project can
/// subscribe to its topics.</para>
///
/// <para>Call it after the change is saved. When the request came from a client
/// with a live connection, that connection is left out, as with core's own
/// broadcasts: it has already applied the change it made. Delivery is
/// best-effort, like everything else on the live connection.</para>
/// </remarks>
public interface ISoloLiveUpdates
{
    Task PublishAsync(Guid projectId, string topic, string kind, string? data = null, CancellationToken ct = default);

    /// <summary>
    /// Pushes a message to one user's own topic, wherever they have the app open
    /// (contract 1.8). The client half hears it through
    /// <c>ISoloLiveTopics.SubscribeUserAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>For things that belong to a person rather than a project — an inbox,
    /// a running timer. A project topic can't do this: any member may subscribe
    /// to any of the project's topics, so <c>"inbox:{someoneElse}"</c> would be
    /// open to all of them. A user topic is joined by the hub on behalf of the
    /// signed-in user only, so nobody else's connection can hold it.</para>
    ///
    /// <para>Otherwise the same as <see cref="PublishAsync"/>: the message is a
    /// nudge, the sending connection is left out, and delivery is best-effort.
    /// The message's <c>ProjectId</c> is empty.</para>
    /// </remarks>
    Task PublishToUserAsync(Guid userId, string topic, string kind, string? data = null, CancellationToken ct = default);
}
