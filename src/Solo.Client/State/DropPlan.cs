using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.State;

/// <summary>
/// What a drop onto a given swimlane × column actually has to do. Pure, so the
/// rule "which endpoint per drop zone" is testable without a browser.
/// </summary>
public readonly record struct DropPlan(bool ChangeEpic, bool ChangeStatus)
{
    /// <summary>
    /// Within a column = re-rank only. Across columns = status. Across swimlanes =
    /// epic (into the ad-hoc band = null). Any card may move to any column — even
    /// one with sub-tasks; the checklist auto-completes the card but never locks it.
    /// </summary>
    public static DropPlan For(TaskDto task, Guid? targetEpicId, TaskState targetStatus) =>
        new(task.EpicId != targetEpicId, task.Status != targetStatus);
}

/// <summary>
/// Turns a drop index into the pair of siblings the server needs to compute a
/// fractional rank.
/// </summary>
public static class RankNeighbors
{
    /// <summary>
    /// <paramref name="others"/> is the destination zone's items in rank order,
    /// excluding the moved one; <paramref name="index"/> is the slot it lands in.
    /// MudBlazor already compensates for a downward move within the same zone, so
    /// the index is directly an insertion point among <paramref name="others"/>.
    /// </summary>
    public static (Guid? Preceding, Guid? Following) Compute(IReadOnlyList<Guid> others, int index)
    {
        if (others.Count == 0)
        {
            // Only card in the zone — its rank is unobservable, so don't churn it.
            return (null, null);
        }

        var slot = Math.Clamp(index, 0, others.Count);
        return (
            slot > 0 ? others[slot - 1] : null,
            slot < others.Count ? others[slot] : null);
    }
}
