using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>
/// Null <see cref="BoardId"/> = a project default: the template new boards are
/// stamped from and a reset returns to, never assignable to an item. Non-null =
/// a board's own label, the only kind items can carry.
/// </summary>
public class Label : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Guid? BoardId { get; set; }
    /// <summary>
    /// The project default this board label was copied from, so a reset can
    /// restore it in place — items keep their label even across a rename. Null
    /// on the defaults themselves and on labels a board invented.
    /// </summary>
    public Guid? SourceId { get; set; }
    public LabelKind Kind { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    /// <summary>Curated icon name (see <c>SoloIcons</c>), or null.</summary>
    public string? Icon { get; set; }
    /// <summary>An uploaded image used in place of the curated <see cref="Icon"/> when set.</summary>
    public Guid? ImageId { get; set; }
    public uint Version { get; set; }

    public Project? Project { get; set; }
    public Board? Board { get; set; }
    public Label? Source { get; set; }
}
