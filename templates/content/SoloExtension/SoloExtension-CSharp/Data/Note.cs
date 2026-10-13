namespace SoloExtension_CSharp.Data;

/// <summary>
/// An example row: a note on a project. Replace it with your own entities, then
/// replace the first migration (see the README).
/// </summary>
public sealed class Note
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    // Core rows are referenced by bare id. A foreign key into core's tables
    // would stop core from ever dropping or renaming them, so tolerate the
    // project having been deleted instead.
    public Guid ProjectId { get; set; }

    public required string Text { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
