namespace Solo.Client.Components;

/// <summary>The board a card belongs to, named for a surface that mixes boards, and the link back to it.</summary>
public sealed record TaskOrigin(string ProjectName, string BoardName, string Href);
