using System.ComponentModel.DataAnnotations;

namespace Solo.DesignDocs.Contracts;

// Request and response shapes, compiled into both halves (the client project
// links this folder), so the package owns its API end to end.

public static class DocsLimits
{
    /// <summary>Vault, folder, note and sketch names: a file name, not a paragraph.</summary>
    public const int Name = 200;

    /// <summary>The markdown copy a writer sends. A note longer than this still edits; its copy stops updating.</summary>
    public const int MarkdownCopy = 1_000_000;

    /// <summary>Headings sent with a copy.</summary>
    public const int Headings = 500;

    /// <summary>An uploaded image, after the browser re-encoded it.</summary>
    public const int ImageBytes = 10 * 1024 * 1024;

    /// <summary>A vault-relative path, as Obsidian writes them.</summary>
    public const int Path = 1024;

    /// <summary>Notes per import call, and their total size, so no one request is huge.</summary>
    public const int ImportNotes = 100;

    public const int ImportBytes = 5 * 1024 * 1024;
}

/// <summary>Live topics, all owned by this extension (the "design-docs:" prefix).</summary>
public static class DocsTopics
{
    public const string Prefix = "design-docs:";

    /// <summary>A project's vault list.</summary>
    public const string Vaults = "design-docs:vaults";

    public static string Vault(Guid vaultId) => $"design-docs:vault:{vaultId:N}";

    /// <summary>One note or sketch: the live relay, and its lifecycle.</summary>
    public static string Document(Guid documentId) => $"design-docs:doc:{documentId:N}";

    public static Guid? DocumentIdOf(string topic) =>
        topic.StartsWith("design-docs:doc:", StringComparison.Ordinal) &&
        Guid.TryParseExact(topic["design-docs:doc:".Length..], "N", out var id)
            ? id
            : null;

    // Kinds published by the REST side.
    public const string Changed = "changed";
    public const string Tree = "tree";
    public const string Closed = "closed";
    public const string Reopened = "reopened";
    public const string Deleted = "deleted";
    public const string Renamed = "renamed";
    public const string Image = "image";
    public const string Edited = "edited";
}

public sealed record VaultDto(
    Guid Id, Guid ProjectId, string Name, bool IsClosed, DateTimeOffset? ClosedAt, DateTimeOffset CreatedAt, uint Version);

/// <summary>A project's vaults, and whether the caller may create, close, reopen and delete them.</summary>
public sealed record VaultListDto(bool CanManage, IReadOnlyList<VaultDto> Vaults);

public sealed record FolderDto(Guid Id, Guid? ParentId, string Name, uint Version);

public sealed record NoteSummaryDto(
    Guid Id, Guid? FolderId, string Name, Guid? LastEditedBy, DateTimeOffset? LastEditedAt, uint Version);

public sealed record SketchSummaryDto(
    Guid Id, Guid NoteId, string Name, string PageSize, string Orientation, string Pattern, uint Version);

public sealed record ImageSummaryDto(Guid Id, string Path, string ContentType, int Width, int Height, long Bytes, uint Version);

/// <summary>Everything the vault page draws its tree and resolves links from.</summary>
public sealed record VaultTreeDto(
    VaultDto Vault,
    bool CanManage,
    IReadOnlyList<FolderDto> Folders,
    IReadOnlyList<NoteSummaryDto> Notes,
    IReadOnlyList<SketchSummaryDto> Sketches,
    IReadOnlyList<ImageSummaryDto> Images);

public sealed record NoteHeadingDto(int Level, string Text, string Slug);

/// <summary>
/// A note's metadata and the last markdown copy a writer sent. The copy can lag
/// the live document; the live document is the truth.
/// </summary>
public sealed record NoteDto(
    Guid Id,
    Guid VaultId,
    Guid? FolderId,
    string Name,
    string Markdown,
    IReadOnlyList<NoteHeadingDto> Headings,
    bool IsSeeded,
    Guid? LastEditedBy,
    string? LastEditedByName,
    DateTimeOffset? LastEditedAt,
    uint Version);

/// <summary>Where a note lives, for a link that carries only its id.</summary>
public sealed record NoteLocationDto(Guid NoteId, Guid ProjectId, string ProjectAlias, Guid VaultId, string Name);

public sealed record LogEntryDto(long Seq, bool IsSnapshot, string Data);

/// <summary>A document's stored update log, for an exact export; plus its seed if nobody has opened it since import.</summary>
public sealed record DocumentLogDto(IReadOnlyList<LogEntryDto> Entries, string? SeedMarkdown);

public sealed record MemberColorDto(Guid UserId, int Slot);

public sealed record TaskLookupDto(Guid Id, string Serial, string Name, bool IsFinished);

public sealed class CreateVaultRequest
{
    [Required, MaxLength(DocsLimits.Name)]
    public string Name { get; init; } = "";
}

public sealed class RenameRequest
{
    [Required, MaxLength(DocsLimits.Name)]
    public string Name { get; init; } = "";

    public uint Version { get; init; }
}

public sealed class CreateFolderRequest
{
    public Guid? ParentId { get; init; }

    [Required, MaxLength(DocsLimits.Name)]
    public string Name { get; init; } = "";
}

/// <summary>Rename and move in one: a note or folder keeps its id either way.</summary>
public sealed class UpdateNodeRequest
{
    [Required, MaxLength(DocsLimits.Name)]
    public string Name { get; init; } = "";

    /// <summary>The folder to sit in; null for the vault's root.</summary>
    public Guid? ParentId { get; init; }

    public uint Version { get; init; }
}

public sealed class CreateNoteRequest
{
    public Guid? FolderId { get; init; }

    [Required, MaxLength(DocsLimits.Name)]
    public string Name { get; init; } = "";
}

public sealed class NoteCopyRequest
{
    [MaxLength(DocsLimits.MarkdownCopy)]
    public string Markdown { get; init; } = "";

    [MaxLength(DocsLimits.Headings)]
    public IReadOnlyList<NoteHeadingDto> Headings { get; init; } = [];

    /// <summary>The newest update the copy includes; an older copy than the stored one is ignored.</summary>
    public long Seq { get; init; }
}

public sealed class CreateSketchRequest
{
    /// <summary>The note that embeds it.</summary>
    public Guid NoteId { get; init; }

    [Required, MaxLength(DocsLimits.Name)]
    public string Name { get; init; } = "";

    public string PageSize { get; init; } = SketchPage.A4;

    public string Orientation { get; init; } = SketchPage.Portrait;

    public string Pattern { get; init; } = SketchPage.Empty;
}

public sealed class UpdateSketchRequest
{
    [Required, MaxLength(DocsLimits.Name)]
    public string Name { get; init; } = "";

    public string PageSize { get; init; } = SketchPage.A4;

    public string Orientation { get; init; } = SketchPage.Portrait;

    public string Pattern { get; init; } = SketchPage.Empty;

    public uint Version { get; init; }
}

public static class SketchPage
{
    public const string A3 = "A3";
    public const string A4 = "A4";
    public const string A5 = "A5";
    public const string Letter = "Letter";
    public const string Portrait = "portrait";
    public const string Landscape = "landscape";
    public const string Empty = "empty";
    public const string Grid = "grid";
    public const string Notebook = "notebook";
    public const string Dots = "dots";

    public static readonly IReadOnlySet<string> Sizes = new HashSet<string> { A3, A4, A5, Letter };
    public static readonly IReadOnlySet<string> Orientations = new HashSet<string> { Portrait, Landscape };
    public static readonly IReadOnlySet<string> Patterns = new HashSet<string> { Empty, Grid, Notebook, Dots };
}

public sealed class LinkLabelsRequest
{
    [MaxLength(200)]
    public IReadOnlyList<string> Urls { get; init; } = [];
}

public sealed class TaskLookupRequest
{
    [MaxLength(200)]
    public IReadOnlyList<Guid> Ids { get; init; } = [];
}

public sealed class ImportNotesRequest
{
    /// <summary>Folder paths to make sure of, '/'-separated, vault-relative.</summary>
    [MaxLength(2000)]
    public IReadOnlyList<string> Folders { get; init; } = [];

    [MaxLength(DocsLimits.ImportNotes)]
    public IReadOnlyList<ImportNote> Notes { get; init; } = [];
}

public sealed class ImportNote
{
    /// <summary>Vault-relative, without the ".md".</summary>
    [Required, MaxLength(DocsLimits.Path)]
    public string Path { get; init; } = "";

    public string Markdown { get; init; } = "";
}

public sealed record ImportResultDto(int FoldersCreated, int NotesCreated, IReadOnlyList<string> Skipped);
