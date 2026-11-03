namespace Solo.Shared.Dtos;

/// <summary>
/// The signed-in user's own work: every task assigned to them on an open board
/// of a project they belong to, with the projects, boards, labels and people
/// needed to draw those tasks the way a board does (contract 1.13).
/// <paramref name="Members"/> holds one entry per distinct user across the
/// projects — enough to put a face to an assignee, not a membership list.
/// <paramref name="Epics"/> and <paramref name="EpicGroups"/> (contract 1.15)
/// are the ones those tasks sit under, and are null from a 1.13 or 1.14 host.
/// </summary>
public sealed record AssignedTasksDto(
    IReadOnlyList<ProjectSummaryDto> Projects,
    IReadOnlyList<BoardDto> Boards,
    IReadOnlyList<LabelDto> Labels,
    IReadOnlyList<ProjectMemberDto> Members,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<EpicDto>? Epics = null,
    IReadOnlyList<EpicGroupDto>? EpicGroups = null);
