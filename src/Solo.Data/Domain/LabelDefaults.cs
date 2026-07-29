using Solo.Data.Entities;
using Solo.Shared;

namespace Solo.Data.Domain;

/// <summary>
/// The project-level label defaults seeded on project creation. They are the
/// template every board's labels start from — see <see cref="BoardLabels"/>.
/// </summary>
public static class LabelDefaults
{
    public static List<Label> ForProject(Guid projectId) =>
    [
        new() { ProjectId = projectId, Kind = LabelKind.Task, Name = "Bug", Color = "#e5484d" },
        new() { ProjectId = projectId, Kind = LabelKind.Task, Name = "Feature", Color = "#46a758" },
        new() { ProjectId = projectId, Kind = LabelKind.Task, Name = "Improvement", Color = "#0091ff" },
        new() { ProjectId = projectId, Kind = LabelKind.SubTask, Name = "Code", Color = "#8e4ec6" },
        new() { ProjectId = projectId, Kind = LabelKind.SubTask, Name = "Asset", Color = "#f5d90a" },
        new() { ProjectId = projectId, Kind = LabelKind.SubTask, Name = "Design", Color = "#e93d82" },
        new() { ProjectId = projectId, Kind = LabelKind.SubTask, Name = "Writing", Color = "#00b8d9" },
    ];
}
