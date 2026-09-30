using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Roadmap.Data;

/// <summary>Takes a deleted task off every roadmap it was on.</summary>
/// <remarks>
/// <para>Event delivery is best-effort, so this is the tidy path rather than the
/// only one: the timeline read also prunes entries whose task has gone.</para>
///
/// <para>A move to another board raises a delete on the board it left, but the
/// task lives on in the same project and its entries stay valid — so an entry
/// goes only once <see cref="ISoloQuery"/> can no longer find the task.</para>
/// </remarks>
public sealed class TaskDeletedHandler(RoadmapDbContext db, ISoloQuery query) : ISoloEventHandler
{
    public IReadOnlySet<string> EntityTypes { get; } = new HashSet<string> { Solo.Shared.Hubs.EntityTypes.Task };

    public async ValueTask HandleAsync(SoloDomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent.Kind != ChangeKind.Deleted ||
            await query.TaskAsync(domainEvent.EntityId, ct) is not null)
        {
            return;
        }

        await db.Entries.Where(e => e.TaskId == domainEvent.EntityId).ExecuteDeleteAsync(ct);
    }
}
