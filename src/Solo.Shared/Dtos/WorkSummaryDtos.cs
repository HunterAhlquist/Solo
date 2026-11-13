namespace Solo.Shared.Dtos;

/// <summary>
/// The signed-in person's work at a glance (contract 1.17): what the iPhone and
/// iPad widgets show, small enough to fetch on a widget's refresh budget. The
/// same tasks as <see cref="AssignedTasksDto"/>, the Your Tasks page's.
/// </summary>
/// <param name="Unstarted">How many assigned tasks are new.</param>
/// <param name="InProgress">How many are in progress.</param>
/// <param name="Finished">
/// How many finished tasks the Your Tasks page shows in its Finished column:
/// those finished in the last 12 days, at most <see cref="WorkSummaryLimits.FinishedShown"/>.
/// </param>
/// <param name="Open">
/// The new and in-progress tasks, soonest deadline first and those without one
/// last, at most <see cref="WorkSummaryLimits.OpenListed"/>.
/// </param>
public sealed record WorkSummaryDto(int Unstarted, int InProgress, int Finished, IReadOnlyList<WorkSummaryTaskDto> Open);

/// <param name="Serial">The task's serial, such as <c>GAME-SP-12</c>.</param>
/// <param name="Path">The task's address in the app, relative to the instance.</param>
public sealed record WorkSummaryTaskDto(Guid Id, string Serial, string Name, TaskState Status, DateOnly? Deadline, string Path);

public static class WorkSummaryLimits
{
    /// <summary>The Finished column shows this many; past it the rest fold away.</summary>
    public const int FinishedShown = 25;

    /// <summary>More than any widget can draw, and a bound on the payload.</summary>
    public const int OpenListed = 100;
}
