using System.ComponentModel.DataAnnotations;
namespace Solo.DesignDocs.Contracts;

public static class DocTopics
{
    public const string Vaults = "design-docs:vaults";
    public static string Vault(Guid id) => $"design-docs:vault:{id:N}";
    public static string Doc(Guid id) => $"design-docs:doc:{id:N}";
}
public sealed record VaultDto(Guid Id, string Name, bool IsClosed, DateTimeOffset? ClosedAt, uint Version);
public sealed record VaultListDto(bool CanManage, IReadOnlyList<VaultDto> Vaults);
public sealed record FolderDto(Guid Id, Guid? ParentId, string Name, uint Version);
public sealed record HeadingDto(string Slug, string Text, int Level);
public sealed record NoteDto(Guid Id, Guid VaultId, Guid? FolderId, string Name, string Markdown,
    string Seq, IReadOnlyList<HeadingDto> Headings, Guid? LastEditedBy, DateTimeOffset? LastEditedAt, uint Version);
public sealed record TreeDto(VaultDto Vault, IReadOnlyList<FolderDto> Folders, IReadOnlyList<NoteDto> Notes);
public sealed record LocateDto(Guid ProjectId, string ProjectAlias, Guid VaultId, Guid NoteId);
public sealed record ColorDto(Guid UserId, int Slot);
public sealed record NameRequest([Required, StringLength(200)] string Name);
public sealed record VaultChange([Required, StringLength(200)] string Name, bool IsClosed, uint Version);
public sealed record ItemChange([Required, StringLength(200)] string Name, Guid? ParentId, uint Version = 0);
public sealed record CopyRequest([Required(AllowEmptyStrings=true), StringLength(1048576)] string Markdown,
    IReadOnlyList<HeadingDto> Headings, string Seq);
public sealed record LabelsRequest([MaxLength(200)] IReadOnlyList<string> Urls);
public sealed record RenameImpactDto(int References);
