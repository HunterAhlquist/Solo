using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Components;

/// <summary>
/// What the task picker shows for a search: boards in the order they arrived,
/// each one's tasks under their epics in the order the board draws them, and
/// the tasks with no epic last.
/// </summary>
public static class TaskPickerSearch
{
    public static IReadOnlyList<TaskPickerBoard> Filter(
        IEnumerable<BoardDetailDto> boards, IReadOnlySet<Guid> exclude, string? query, TaskPickerFilter filter)
    {
        var text = query?.Trim() ?? string.Empty;
        var results = new List<TaskPickerBoard>();
        foreach (var board in boards.Where(b => filter.ClosedBoards || b.IsOpen))
        {
            var epics = InBoardOrder(board);
            var known = epics.ToDictionary(e => e.Id);

            // A task naming an epic the board doesn't list (a delete crossing
            // this read) would otherwise vanish, so it counts as having none.
            Guid? EpicOf(TaskDto task) => task.EpicId is { } id && known.ContainsKey(id) ? id : null;

            var shown = board.Tasks
                .Where(t => !exclude.Contains(t.Id))
                .Where(t => filter.Finished || t.Status != TaskState.Finished)
                .Where(t => text.Length == 0 ||
                            t.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                            Serial(board, t).Contains(text, StringComparison.OrdinalIgnoreCase) ||
                            (EpicOf(t) is { } epic &&
                             known[epic].Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(t => t.Rank)
                .ToLookup(EpicOf);

            var lanes = epics
                .Select(e => new TaskPickerEpic(e, shown[e.Id].ToList()))
                .Append(new TaskPickerEpic(null, shown[null].ToList()))
                .Where(l => l.Tasks.Count > 0)
                .ToList();
            if (lanes.Count > 0)
            {
                results.Add(new TaskPickerBoard(board, lanes));
            }
        }

        return results;
    }

    /// <summary>
    /// The board page's top-to-bottom order (its <c>TopLevelItems</c>): groups
    /// and ungrouped epics share one rank order, and a group's epics follow
    /// their own ranks inside it.
    /// </summary>
    public static IReadOnlyList<EpicDto> InBoardOrder(BoardDetailDto board) =>
        board.Groups.Select(g => (g.Id, g.Rank, IsGroup: true))
            .Concat(board.Epics.Where(e => e.GroupId is null).Select(e => (e.Id, e.Rank, IsGroup: false)))
            .OrderBy(i => i.Rank).ThenBy(i => i.Id)
            .SelectMany(i => i.IsGroup
                ? board.Epics.Where(e => e.GroupId == i.Id).OrderBy(e => e.Rank)
                : board.Epics.Where(e => e.Id == i.Id))
            .ToList();

    public static string Serial(BoardDetailDto board, TaskDto task) =>
        Aliases.Serial(board.ProjectAlias, board.Alias, task.Number);
}

/// <summary>A board's share of the results; only epics with a task to show.</summary>
public sealed record TaskPickerBoard(BoardDetailDto Board, IReadOnlyList<TaskPickerEpic> Epics);

/// <summary>An epic's tasks in board order. <see cref="Epic"/> is null for the tasks in none.</summary>
public sealed record TaskPickerEpic(EpicDto? Epic, IReadOnlyList<TaskDto> Tasks);

/// <summary>What the picker shows beyond open boards' unfinished tasks.</summary>
public sealed record TaskPickerFilter(bool Finished = false, bool ClosedBoards = false)
{
    public static TaskPickerFilter Default { get; } = new();
}
