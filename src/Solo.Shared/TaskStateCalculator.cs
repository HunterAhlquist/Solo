namespace Solo.Shared;

public static class TaskStateCalculator
{
    /// <summary>
    /// A task without sub-tasks keeps its manually set status. With sub-tasks the
    /// status is auto-derived: all finished = Finished, some finished = InProgress,
    /// none finished = Unstarted.
    /// </summary>
    /// <remarks>
    /// Lives in Solo.Shared so the client can apply the identical roll-up
    /// optimistically when a checklist box is ticked. The server's answer always
    /// wins, but the two must never disagree — hence one implementation.
    /// </remarks>
    public static TaskState Derive(int subTaskCount, int finishedCount, TaskState manualStatus)
    {
        if (subTaskCount == 0)
        {
            return manualStatus;
        }

        if (finishedCount >= subTaskCount)
        {
            return TaskState.Finished;
        }

        return finishedCount > 0 ? TaskState.InProgress : TaskState.Unstarted;
    }
}
