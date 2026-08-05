using Solo.Data.Entities;

namespace Solo.Data.Domain;

/// <summary>
/// Every board owns its labels; the project's defaults (see
/// <see cref="LabelDefaults"/>) are the template a new board is stamped from
/// and the target a reset reconciles back to.
/// </summary>
public static class BoardLabels
{
    /// <summary>Fresh board-owned copies of the project defaults.</summary>
    public static List<Label> CopyToBoard(IEnumerable<Label> defaults, Guid boardId) =>
        defaults.Select(d => Copy(d, boardId)).ToList();

    /// <summary>
    /// Reconciles a board's labels back to the project defaults. A copy is
    /// matched to the default it came from, or — for a label the board made
    /// itself — by kind and name, and is then restored in place, so items keep
    /// their assignment even across a rename. Everything else is created or
    /// dropped; the caller persists the returned sets.
    /// </summary>
    public static LabelResetResult ResetToDefaults(
        IEnumerable<Label> defaults, IEnumerable<Label> current, Guid boardId)
    {
        var surplus = current.ToList();
        var added = new List<Label>();

        foreach (var d in defaults)
        {
            var match = surplus.FirstOrDefault(l => l.SourceId == d.Id)
                        ?? surplus.FirstOrDefault(l => l.SourceId is null && l.Kind == d.Kind &&
                                                       string.Equals(l.Name, d.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                added.Add(Copy(d, boardId));
                continue;
            }

            surplus.Remove(match);
            match.SourceId = d.Id;
            match.Name = d.Name;
            match.Color = d.Color;
            match.Icon = d.Icon;
            match.ImageId = d.ImageId;
        }

        return new LabelResetResult(added, surplus);
    }

    /// <summary>
    /// This label's equivalent among another board's labels, for an item moving
    /// between the two: the copy stamped from the same project default, or
    /// failing that one of the same kind and name — the matching a reset uses.
    /// Null when that board has nothing like it, and the item loses the label.
    /// </summary>
    public static Label? Counterpart(Label label, IEnumerable<Label> candidates)
    {
        var sameKind = candidates.Where(c => c.Kind == label.Kind).ToList();
        return (label.SourceId is { } sourceId
                   ? sameKind.FirstOrDefault(c => c.SourceId == sourceId)
                   : null)
               ?? sameKind.FirstOrDefault(c =>
                   string.Equals(c.Name, label.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static Label Copy(Label source, Guid boardId) => new()
    {
        ProjectId = source.ProjectId,
        BoardId = boardId,
        SourceId = source.Id,
        Kind = source.Kind,
        Name = source.Name,
        Color = source.Color,
        Icon = source.Icon,
        ImageId = source.ImageId,
    };
}

/// <summary>
/// What a reset changes beyond the in-place updates: labels to insert and
/// labels no longer in the defaults, which the caller deletes.
/// </summary>
public sealed record LabelResetResult(IReadOnlyList<Label> Added, IReadOnlyList<Label> Removed);
