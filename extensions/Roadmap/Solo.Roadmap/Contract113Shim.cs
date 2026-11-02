// TEMPORARY: stands in for contract 1.13's ISoloQuery.AssignedTasksAsync and
// AssignedTasksDto until the dashboard branch publishes them. Delete this file
// when the package references move to 1.13.
using Solo.Extensibility;

namespace Solo.Shared.Dtos
{
    public sealed record AssignedTasksDto(
        IReadOnlyList<ProjectSummaryDto> Projects,
        IReadOnlyList<BoardDto> Boards,
        IReadOnlyList<LabelDto> Labels,
        IReadOnlyList<ProjectMemberDto> Members,
        IReadOnlyList<TaskDto> Tasks);
}

namespace Solo.Roadmap
{
    internal static class Contract113Shim
    {
        public static async Task<Shared.Dtos.AssignedTasksDto> AssignedTasksAsync(
            this ISoloQuery query, CancellationToken ct = default)
        {
            var caller = await query.CallerAsync(ct);
            var projects = await query.ProjectsAsync(ct);
            var boards = new List<Shared.Dtos.BoardDto>();
            var tasks = new List<Shared.Dtos.TaskDto>();
            foreach (var project in projects)
            {
                foreach (var board in (await query.BoardsAsync(project.Id, ct)).Where(b => b.IsOpen))
                {
                    boards.Add(board);
                    tasks.AddRange((await query.TasksAsync(board.Id, ct)).Where(t => t.AssigneeIds.Contains(caller.UserId)));
                }
            }

            return new Shared.Dtos.AssignedTasksDto(projects, boards, [], [], tasks);
        }
    }
}
