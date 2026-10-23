using Solo.Extensibility;

namespace Solo.Extension.Fixture;

/// <summary>
/// Answers client-sent messages on <c>fixture:</c> topics so the hub's routing
/// can be tested end to end. Nothing in core registers a handler of its own.
/// </summary>
public sealed class FixtureLiveHandler : ISoloLiveMessageHandler
{
    public async ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct)
    {
        switch (message.Kind)
        {
            case "echo":
                await reply.ToSenderAsync("echo", message.Data, ct);
                break;
            case "relay":
                await reply.ToOthersAsync("relayed", message.Data, ct);
                break;
            case "whoami":
                await reply.ToSenderAsync("whoami",
                    $"{message.Sender.Role}|{message.Sender.CanWrite}|{message.Sender.DisplayName}", ct);
                break;
            case "throw":
                throw new InvalidOperationException("The fixture handler was asked to throw.");
            case "slow":
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                break;
        }
    }
}
