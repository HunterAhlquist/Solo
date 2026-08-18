namespace Solo.Extensibility;

/// <summary>
/// The operations extensions may hook. These ids are the stable contract —
/// hooks never bind to a controller signature, so renaming an action or its
/// parameters stays an internal change.
/// </summary>
/// <remarks>
/// An action is hookable only when it carries <c>[SoloOperation]</c>. Adding an
/// id here is a deliberate act of opening a write path, not a side effect of
/// writing a controller.
/// </remarks>
public static class Operations
{
    public const string TaskCreate = "task.create";
    public const string TaskUpdate = "task.update";
    public const string TaskDelete = "task.delete";
    public const string TaskSetStatus = "task.setStatus";
    public const string TaskSetEpic = "task.setEpic";
    public const string TaskMove = "task.move";

    public const string EpicCreate = "epic.create";
    public const string EpicUpdate = "epic.update";
    public const string EpicDelete = "epic.delete";

    public const string SubTaskCreate = "subtask.create";
    public const string SubTaskUpdate = "subtask.update";
    public const string SubTaskDelete = "subtask.delete";
    public const string SubTaskSetFinished = "subtask.setFinished";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        TaskCreate, TaskUpdate, TaskDelete, TaskSetStatus, TaskSetEpic, TaskMove,
        EpicCreate, EpicUpdate, EpicDelete,
        SubTaskCreate, SubTaskUpdate, SubTaskDelete, SubTaskSetFinished,
    };
}

/// <summary>Marks a core action as hookable under the given operation id.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SoloOperationAttribute(string operation) : Attribute
{
    public string Operation { get; } = operation;
}
