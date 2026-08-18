using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Fans a committed change out to extension handlers.
/// </summary>
/// <remarks>
/// <para>Core raises these at the same call sites that already broadcast over
/// SignalR — those know the board/project scope and the changed fields, which a
/// ChangeTracker-derived event would not.</para>
///
/// <para>Every handler is wrapped and timed out, and a failure is logged and
/// swallowed: an extension must not be able to 500 a core write. Delivery is
/// in-process and best-effort — there is no outbox — so "never miss an event"
/// is explicitly not promised.</para>
/// </remarks>
public interface IDomainEventPublisher
{
    Task PublishAsync(SoloDomainEvent domainEvent, CancellationToken ct = default);
}

public sealed class DomainEventPublisher(
    IServiceProvider services,
    ExtensionState state,
    IOptions<ExtensionOptions> options,
    ILogger<DomainEventPublisher> logger) : IDomainEventPublisher
{
    public async Task PublishAsync(SoloDomainEvent domainEvent, CancellationToken ct = default)
    {
        if (state.Packages.Count == 0)
        {
            return;
        }

        var handlers = services.GetServices<ISoloEventHandler>()
            .Where(h => h.EntityTypes.Count == 0 || h.EntityTypes.Contains(domainEvent.EntityType))
            .ToList();

        foreach (var handler in handlers)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.NotificationTimeout);

            try
            {
                await handler.HandleAsync(domainEvent, timeout.Token);
            }
            catch (Exception e)
            {
                logger.LogError(
                    e, "Extension event handler {Handler} failed on {EntityType} {EntityId}; ignored.",
                    handler.GetType().FullName, domainEvent.EntityType, domainEvent.EntityId);
            }
        }
    }
}
