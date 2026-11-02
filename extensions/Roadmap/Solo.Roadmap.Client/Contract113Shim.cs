// TEMPORARY: stands in for contract 1.13's AssignedTasksDto, DashboardSection
// seam and assigned-tasks topic until the dashboard branch publishes them.
// Delete this file when the package references move to 1.13.
namespace Solo.Shared.Dtos
{
    public sealed record AssignedTasksDto(
        IReadOnlyList<ProjectSummaryDto> Projects,
        IReadOnlyList<BoardDto> Boards,
        IReadOnlyList<LabelDto> Labels,
        IReadOnlyList<ProjectMemberDto> Members,
        IReadOnlyList<TaskDto> Tasks);
}

namespace Solo.Shared.Extensibility
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DashboardSectionAttribute(string title) : Attribute
    {
        public string Title { get; } = title;

        public int Order { get; set; }
    }
}

namespace Solo.Roadmap.Client
{
    internal static class Contract113Topics
    {
        public const string AssignedTasks = "solo:assigned-tasks";
    }
}
