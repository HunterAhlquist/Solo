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
}
