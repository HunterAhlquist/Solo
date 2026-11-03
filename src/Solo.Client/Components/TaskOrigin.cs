namespace Solo.Client.Components;

/// <summary>
/// Where a card lives, for a surface that mixes boards: its project and board,
/// the link back to that board, and the "Group • Epic" line placing it on the
/// board, or null for a task outside any epic.
/// </summary>
public sealed record TaskOrigin(string ProjectName, string BoardName, string Href, string? EpicLine = null);
