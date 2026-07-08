using Solo.Data.Entities;
using Solo.Shared;

namespace Solo.Data.Domain;

/// <summary>Resolves which labels apply on a given board: project-global + board-local.</summary>
public static class LabelScope
{
    public static bool IsVisibleOn(Label label, Guid boardId) =>
        label.BoardId is null || label.BoardId == boardId;

    public static IEnumerable<Label> ResolveForBoard(
        IEnumerable<Label> projectLabels, Guid boardId, LabelKind? kind = null) =>
        projectLabels.Where(l => IsVisibleOn(l, boardId) && (kind is null || l.Kind == kind));

    /// <summary>A label is assignable to an item only from its own board's scope and kind.</summary>
    public static bool IsAssignable(Label label, Guid boardId, LabelKind kind) =>
        IsVisibleOn(label, boardId) && label.Kind == kind;
}
