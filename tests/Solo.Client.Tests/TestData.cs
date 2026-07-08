using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

internal static class TestData
{
    public static readonly Guid BoardId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid EpicId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid MeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid OtherId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static TaskDto Task(
        Guid? id = null,
        Guid? epicId = null,
        TaskState status = TaskState.Unstarted,
        decimal rank = 1024m,
        IReadOnlyList<SubTaskDto>? subTasks = null,
        Guid? labelId = null,
        IReadOnlyList<Guid>? assignees = null) =>
        new(
            id ?? Guid.NewGuid(),
            BoardId,
            epicId,
            "Task",
            labelId,
            null,
            status,
            rank,
            1,
            assignees ?? [],
            subTasks ?? []);

    public static SubTaskDto SubTask(bool finished, Guid? taskId = null) =>
        new(Guid.NewGuid(), taskId ?? Guid.NewGuid(), "Sub", null, finished, 1024m, 1);

    public static EpicDto Epic(Guid? id = null, decimal rank = 1024m) =>
        new(id ?? EpicId, BoardId, "Epic", null, rank, 1, []);

    public static LabelDto Label(Guid id, string name = "Bug", string color = "#f56565") =>
        new(id, ProjectId, null, LabelKind.Task, name, color, 1);

    public static ProjectMemberDto Member(Guid userId, string name = "Hunter") =>
        new(Guid.NewGuid(), userId, $"{name.ToLowerInvariant()}@example.com", name, null,
            ProjectRole.ReadWrite, false, false);

    public static BoardDetailDto Board(
        IReadOnlyList<TaskDto>? tasks = null,
        IReadOnlyList<EpicDto>? epics = null,
        IReadOnlyList<LabelDto>? labels = null) =>
        new(BoardId, ProjectId, "Board", null, true, 1, epics ?? [], tasks ?? [], labels ?? []);
}
