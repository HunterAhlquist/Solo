using Solo.Shared.Dtos;

namespace Solo.Extensibility;

/// <summary>
/// Read access to core data, already scoped to the current caller.
/// </summary>
/// <remarks>
/// <para>Extensions never get <c>SoloDbContext</c>. Two reasons, and the second
/// is the one that matters: it would couple every extension to the entity model
/// (the Task → TaskItem rename would have broken all of them), and it would
/// bypass <c>ProjectAccessHandler</c> entirely, so an extension querying
/// <c>db.Tasks</c> would see every project on the instance.</para>
///
/// <para>Every method here filters by the caller's project membership before
/// returning a row, so an extension gets correct authorization by construction
/// and cannot accidentally build a hole.</para>
/// </remarks>
public interface ISoloQuery
{
    /// <summary>Projects the caller can read. Empty for a caller who is a member of none.</summary>
    Task<IReadOnlyList<ProjectSummaryDto>> ProjectsAsync(CancellationToken ct = default);

    /// <summary>Boards of a project, or empty when the caller cannot read that project.</summary>
    Task<IReadOnlyList<BoardDto>> BoardsAsync(Guid projectId, CancellationToken ct = default);

    Task<IReadOnlyList<EpicDto>> EpicsAsync(Guid boardId, CancellationToken ct = default);

    Task<IReadOnlyList<TaskDto>> TasksAsync(Guid boardId, CancellationToken ct = default);

    /// <summary>A single task, or null when it doesn't exist or the caller can't read it.</summary>
    Task<TaskDto?> TaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>Identities of a readable task's assignees; empty if access is denied.</summary>
    Task<IReadOnlyList<AssigneeIdentity>> TaskAssigneeIdentitiesAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>True when the caller may write to the project owning this board.</summary>
    Task<bool> CanWriteBoardAsync(Guid boardId, CancellationToken ct = default);
}

public sealed record AssigneeIdentity(Guid UserId, string Issuer, string Subject);
