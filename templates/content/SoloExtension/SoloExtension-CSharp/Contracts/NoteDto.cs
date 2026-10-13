namespace SoloExtension_CSharp;

/// <summary>
/// A note as the API returns it. The entity stays on the server: what crosses
/// the API is this.
/// </summary>
public sealed record NoteDto(Guid Id, string Text, Guid CreatedByUserId, DateTimeOffset CreatedAt);

public sealed record CreateNoteRequest(string Text);
