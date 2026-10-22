using System.Text.Json;
using Solo.Extensibility;

namespace Solo.Extension.Fixture;

public sealed class FixtureLiveHandler : ISoloLiveMessageHandler
{
    public async ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct)
    {
        if (message.Kind == "sender")
            await reply.ToSenderAsync("sender", JsonSerializer.Serialize(message.Sender), ct);
        else if (message.Kind == "fail")
            throw new InvalidOperationException("Fixture failure");
        else if (message.Kind == "slow")
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        else
            await reply.ToOthersAsync(message.Kind, message.Data, ct);
    }
}
