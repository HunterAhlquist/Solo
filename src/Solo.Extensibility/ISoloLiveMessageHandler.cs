using Solo.Shared;

namespace Solo.Extensibility;

/// <summary>
/// Receives what clients send on this extension's live topics (contract 1.10):
/// the other direction from <see cref="ISoloLiveUpdates"/>. A client half sends
/// through <c>ISoloLiveTopics.SendAsync</c>; core checks the sender holds the
/// topic, meters them, and hands the message here. What happens next is the
/// handler's call: drop it, answer the sender, or relay it to everyone else.
/// </summary>
/// <remarks>
/// <para>A topic belongs to the extension whose id comes before its first
/// <c>:</c>, so <c>"roadmap:{id}"</c> is Roadmap's. A message on a topic whose
/// extension registered no handler, or is disabled, is refused before any
/// handler runs: client-sent messages are opt-in per extension, and one
/// extension can never hear another's.</para>
///
/// <para>Register it with <see cref="ExtensionServiceCollectionExtensions.AddLiveMessageHandler{THandler}"/>.
/// It is resolved per message from the hub invocation's scope, so it may take
/// the extension's own <c>DbContext</c>. Don't rely on anything request-shaped:
/// a hub invocation has no route, so <c>ISoloQuery</c> and the project policies
/// have nothing to resolve. Everything known about the sender is on
/// <see cref="LiveMessage.Sender"/>.</para>
///
/// <para>Subscribing is where read access was checked, so every sender can read
/// the project. Whether they may <em>change</em> anything is the handler's to
/// decide from <see cref="LiveSender.CanWrite"/>, which core keeps current when
/// a member's role changes.</para>
///
/// <para>Core gives a handler about five seconds. One that throws or runs over
/// is logged naming the extension, and the sender is told it failed.</para>
/// </remarks>
public interface ISoloLiveMessageHandler
{
    /// <summary>
    /// Runs once per client-sent message on one of this extension's topics.
    /// Not calling a method on <paramref name="reply"/> drops the message.
    /// </summary>
    ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct);
}

/// <summary>A message a client sent on a project topic it holds.</summary>
/// <param name="Kind">1–64 characters, the extension's own vocabulary.</param>
/// <param name="Data">
/// At most <c>BoardHubConstants.ClientMessageMaxLength</c> characters. Core's
/// hub speaks JSON, so binary payloads travel as base64.
/// </param>
public sealed record LiveMessage(Guid ProjectId, string Topic, string Kind, string? Data, LiveSender Sender);

/// <summary>
/// Who sent a message. Core resolves this when the topic is subscribed and
/// refreshes it when the member's role changes, so a handler never has to look
/// it up per message.
/// </summary>
public sealed record LiveSender(Guid UserId, string? DisplayName, ProjectRole Role, bool IsProjectOwner)
{
    /// <summary>The project's own test for writing: a ReadWrite member, or an owner.</summary>
    public bool CanWrite => IsProjectOwner || Role == ProjectRole.ReadWrite;
}

/// <summary>Where a handler's answer goes. Each call is a <c>TopicMessage</c> carrying the sender as its actor.</summary>
public interface ILiveMessageReply
{
    /// <summary>Only to the connection that sent the message.</summary>
    Task ToSenderAsync(string kind, string? data = null, CancellationToken ct = default);

    /// <summary>To every other connection on the topic, the sender's own other tabs included.</summary>
    Task ToOthersAsync(string kind, string? data = null, CancellationToken ct = default);
}
