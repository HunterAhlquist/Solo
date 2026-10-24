using Solo.Shared;

namespace Solo.Extensibility;

/// <summary>Receives client messages on the registering extension's project topics (contract 1.10).</summary>
public interface ISoloLiveMessageHandler
{
    /// <summary>Handles one message. Send replies explicitly and honor cancellation.</summary>
    ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct);
}

public sealed record LiveMessage(Guid ProjectId, string Topic, string Kind, string? Data, LiveSender Sender);

/// <summary>Identity and access resolved at subscription, refreshed when membership changes.</summary>
public sealed record LiveSender(Guid UserId, string? DisplayName, ProjectRole Role, bool IsProjectOwner)
{
    /// <summary>An opaque connection key for multipart state and leases. It is not a credential.</summary>
    public string ConnectionId { get; init; } = string.Empty;
}

public interface ILiveMessageReply
{
    /// <summary>Only the connection that sent this message.</summary>
    Task ToSenderAsync(string kind, string? data = null, CancellationToken ct = default);

    /// <summary>Every other connection on the topic, including the sender's other tabs.</summary>
    Task ToOthersAsync(string kind, string? data = null, CancellationToken ct = default);
}

/// <summary>Records a handler type without constructing unrelated extensions' scoped services.</summary>
public sealed record LiveMessageHandlerRegistration(Type HandlerType);
