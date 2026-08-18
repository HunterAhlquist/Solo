using Solo.Shared;

namespace Solo.Extensibility;

/// <summary>
/// Notification of a committed change. No veto — that is what
/// <see cref="ISoloOperationHook"/> is for.
/// </summary>
/// <remarks>
/// Dispatched after commit and out of band, each handler wrapped and timed out,
/// failures logged and never propagated: an extension must not be able to 500 a
/// core write. Delivery is in-process and best-effort — there is no outbox, so a
/// handler that must never miss an event is not something this can promise.
/// </remarks>
public interface ISoloEventHandler
{
    /// <summary>Entity types this handler wants, from <c>Solo.Shared.Hubs.EntityTypes</c>. Empty means all.</summary>
    IReadOnlySet<string> EntityTypes => new HashSet<string>();

    ValueTask HandleAsync(SoloDomainEvent domainEvent, CancellationToken ct);
}

/// <summary>A committed change to a core entity.</summary>
public sealed record SoloDomainEvent(
    string EntityType,
    Guid EntityId,
    ChangeKind Kind,
    IReadOnlyList<string> ChangedFields,
    Guid ActorUserId,
    Guid? ProjectId,
    Guid? BoardId);
