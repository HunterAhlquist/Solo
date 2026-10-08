using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;
using Solo.Roadmap.Contracts;
using Solo.Shared;

namespace Solo.Roadmap.Data;

/// <summary>
/// Keeps roadmaps in step with their tasks: takes a deleted task off every
/// roadmap it was on, and tells open roadmap pages when a task on them changes.
/// </summary>
/// <remarks>
/// <para>Event delivery is best-effort, so this is the tidy path rather than the
/// only one: the timeline read also prunes entries whose task has gone.</para>
///
/// <para>A move to another board raises a delete on the board it left, but the
/// task lives on in the same project and its entries stay valid — so an entry
/// goes only once <see cref="ISoloQuery"/> can no longer find the task. Either
/// way the roadmaps that held it hear about it: they lost a row, or the row's
/// board changed.</para>
///
/// <para>Runs inside the request that made the change, after it's saved, so the
/// person who made it is left out of the message as usual.</para>
/// </remarks>
public sealed class TaskEventsHandler(RoadmapDbContext db, ISoloQuery query, ISoloLiveUpdates live)
    : ISoloEventHandler
{
    public IReadOnlySet<string> EntityTypes { get; } = new HashSet<string> { Solo.Shared.Hubs.EntityTypes.Task };

    public async ValueTask HandleAsync(SoloDomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent.Kind == ChangeKind.Created)
        {
            return;
        }

        var holding = await db.Entries.AsNoTracking()
            .Where(e => e.TaskId == domainEvent.EntityId)
            .Select(e => new { e.RoadmapId, e.Roadmap.ProjectId })
            .Distinct()
            .ToListAsync(ct);
        if (holding.Count == 0)
        {
            return;
        }

        if (domainEvent.Kind == ChangeKind.Deleted)
        {
            if (await query.TaskAsync(domainEvent.EntityId, ct) is null)
            {
                await db.Entries.Where(e => e.TaskId == domainEvent.EntityId).ExecuteDeleteAsync(ct);
            }

            foreach (var roadmap in holding)
            {
                await live.PublishAsync(roadmap.ProjectId, RoadmapTopics.Roadmap(roadmap.RoadmapId),
                    RoadmapTopics.EntriesChanged, ct: ct);
            }

            return;
        }

        foreach (var roadmap in holding)
        {
            await live.PublishAsync(roadmap.ProjectId, RoadmapTopics.Roadmap(roadmap.RoadmapId),
                RoadmapTopics.TaskChanged, domainEvent.EntityId.ToString(), ct);
        }
    }
}
