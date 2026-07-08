using Solo.Shared;

namespace Solo.Client.Components;

/// <summary>
/// The board is a grid of epic swimlanes × status columns, so a drop zone is
/// identified by the pair. Encoded as <c>{epicId|adhoc}:{status}</c> — a GUID
/// never contains a colon, so the split is unambiguous.
/// </summary>
public static class BoardZones
{
    /// <summary>The band for tasks that belong to no epic.</summary>
    public const string AdHoc = "adhoc";

    public static readonly TaskState[] Statuses =
        [TaskState.Unstarted, TaskState.InProgress, TaskState.Finished];

    /// <summary>Past this many cards, a Finished zone folds its overflow away.</summary>
    public const int FinishedVisibleLimit = 25;

    public static string Id(Guid? epicId, TaskState status) =>
        $"{(epicId?.ToString() ?? AdHoc)}:{(int)status}";

    public static bool TryParse(string zoneId, out Guid? epicId, out TaskState status)
    {
        epicId = null;
        status = TaskState.Unstarted;

        var separator = zoneId.LastIndexOf(':');
        if (separator < 0 ||
            !int.TryParse(zoneId[(separator + 1)..], out var raw) ||
            !Enum.IsDefined((TaskState)raw))
        {
            return false;
        }

        status = (TaskState)raw;
        var epicPart = zoneId[..separator];
        if (epicPart == AdHoc)
        {
            return true;
        }

        if (!Guid.TryParse(epicPart, out var parsed))
        {
            return false;
        }

        epicId = parsed;
        return true;
    }

    public static string Title(TaskState status) => status switch
    {
        TaskState.Unstarted => "Unstarted",
        TaskState.InProgress => "In Progress",
        TaskState.Finished => "Finished",
        _ => status.ToString(),
    };
}
