using Solo.Extensibility;
using Solo.Shared;

namespace Solo.DesignDocs;

/// <summary>B0's throwaway relay. No persistence: this branch must not be merged as the feature.</summary>
public sealed class PrototypeRelay : ISoloLiveMessageHandler
{
    public async ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct)
    {
        if (message.Topic != "design-docs:prototype") return;
        var writer = message.Sender.IsProjectOwner || message.Sender.Role == ProjectRole.ReadWrite;
        if (message.Kind == "sync" || writer && message.Kind is "update" or "state" or "awareness")
        {
            await reply.ToOthersAsync(message.Kind, message.Data, ct);
            if (message.Kind == "update") await reply.ToSenderAsync("ack", message.Data, ct);
        }
        else if (message.Kind == "update") await reply.ToSenderAsync("refused", "Read-only member", ct);
    }
}
