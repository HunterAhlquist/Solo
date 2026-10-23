using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Client;

/// <summary>
/// The calls the Design Docs pages make, over <see cref="ISoloClientApi"/>, the
/// host's own authenticated client. Constructed per component: a client half
/// can't register services, since its assembly loads after the container is built.
/// </summary>
public sealed class DocsApi(ISoloClientApi api)
{
    private const string Base = "api/ext/design-docs";

    private static readonly string PackageVersion =
        typeof(DocsApi).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>A file in the package's content/ folder, versioned so a cached script never runs beside a newer page.</summary>
    public Uri Content(string fileName) =>
        api.Resolve($"api/extensions/design-docs/content/{fileName}?v={PackageVersion}");

    public Uri Avatar(Guid userId) => api.Resolve($"api/users/{userId}/avatar");

    private static string Vault(Guid projectId, Guid vaultId) => $"{Base}/projects/{projectId}/vaults/{vaultId}";

    // -- Vaults ----------------------------------------------------------------

    public Task<VaultListDto> VaultsAsync(Guid projectId, CancellationToken ct = default) =>
        api.GetAsync<VaultListDto>($"{Base}/projects/{projectId}/vaults", ct);

    public Task<VaultDto> CreateVaultAsync(Guid projectId, string name, CancellationToken ct = default) =>
        api.SendAsync<VaultDto>(HttpMethod.Post, $"{Base}/projects/{projectId}/vaults", new CreateVaultRequest { Name = name }, ct);

    public Task<VaultDto> RenameVaultAsync(Guid projectId, Guid vaultId, string name, uint version, CancellationToken ct = default) =>
        api.SendAsync<VaultDto>(HttpMethod.Put, Vault(projectId, vaultId), new RenameRequest { Name = name, Version = version }, ct);

    public Task<VaultDto> CloseVaultAsync(Guid projectId, Guid vaultId, CancellationToken ct = default) =>
        api.SendAsync<VaultDto>(HttpMethod.Post, $"{Vault(projectId, vaultId)}/close", null, ct);

    public Task<VaultDto> ReopenVaultAsync(Guid projectId, Guid vaultId, CancellationToken ct = default) =>
        api.SendAsync<VaultDto>(HttpMethod.Post, $"{Vault(projectId, vaultId)}/reopen", null, ct);

    public Task DeleteVaultAsync(Guid projectId, Guid vaultId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, Vault(projectId, vaultId), null, ct);

    public Task<VaultTreeDto> TreeAsync(Guid projectId, Guid vaultId, CancellationToken ct = default) =>
        api.GetAsync<VaultTreeDto>($"{Vault(projectId, vaultId)}/tree", ct);

    // -- Folders and notes -----------------------------------------------------

    public Task<FolderDto> CreateFolderAsync(Guid projectId, Guid vaultId, Guid? parentId, string name, CancellationToken ct = default) =>
        api.SendAsync<FolderDto>(HttpMethod.Post, $"{Vault(projectId, vaultId)}/folders",
            new CreateFolderRequest { ParentId = parentId, Name = name }, ct);

    public Task<FolderDto> UpdateFolderAsync(Guid projectId, Guid vaultId, Guid folderId, string name, Guid? parentId,
        uint version, CancellationToken ct = default) =>
        api.SendAsync<FolderDto>(HttpMethod.Put, $"{Vault(projectId, vaultId)}/folders/{folderId}",
            new UpdateNodeRequest { Name = name, ParentId = parentId, Version = version }, ct);

    public Task DeleteFolderAsync(Guid projectId, Guid vaultId, Guid folderId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Vault(projectId, vaultId)}/folders/{folderId}", null, ct);

    public Task<NoteSummaryDto> CreateNoteAsync(Guid projectId, Guid vaultId, Guid? folderId, string name, CancellationToken ct = default) =>
        api.SendAsync<NoteSummaryDto>(HttpMethod.Post, $"{Vault(projectId, vaultId)}/notes",
            new CreateNoteRequest { FolderId = folderId, Name = name }, ct);

    public Task<NoteDto> NoteAsync(Guid projectId, Guid vaultId, Guid noteId, CancellationToken ct = default) =>
        api.GetAsync<NoteDto>($"{Vault(projectId, vaultId)}/notes/{noteId}", ct);

    public Task<NoteSummaryDto> UpdateNoteAsync(Guid projectId, Guid vaultId, Guid noteId, string name, Guid? folderId,
        uint version, CancellationToken ct = default) =>
        api.SendAsync<NoteSummaryDto>(HttpMethod.Put, $"{Vault(projectId, vaultId)}/notes/{noteId}",
            new UpdateNodeRequest { Name = name, ParentId = folderId, Version = version }, ct);

    public Task DeleteNoteAsync(Guid projectId, Guid vaultId, Guid noteId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Vault(projectId, vaultId)}/notes/{noteId}", null, ct);

    public Task SaveCopyAsync(Guid projectId, Guid vaultId, Guid noteId, NoteCopyRequest copy, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Vault(projectId, vaultId)}/notes/{noteId}/copy", copy, ct);

    public Task<int> ReferencesAsync(Guid projectId, Guid vaultId, Guid noteId, CancellationToken ct = default) =>
        api.GetAsync<int>($"{Vault(projectId, vaultId)}/notes/{noteId}/references", ct);

    public Task<DocumentLogDto> LogAsync(Guid projectId, Guid vaultId, Guid documentId, CancellationToken ct = default) =>
        api.GetAsync<DocumentLogDto>($"{Vault(projectId, vaultId)}/documents/{documentId}/log", ct);

    public Task<ImportResultDto> ImportNotesAsync(Guid projectId, Guid vaultId, ImportNotesRequest request, CancellationToken ct = default) =>
        api.SendAsync<ImportResultDto>(HttpMethod.Post, $"{Vault(projectId, vaultId)}/import/notes", request, ct);

    // -- Sketches and images ---------------------------------------------------

    public Task<SketchSummaryDto> CreateSketchAsync(Guid projectId, Guid vaultId, CreateSketchRequest request, CancellationToken ct = default) =>
        api.SendAsync<SketchSummaryDto>(HttpMethod.Post, $"{Vault(projectId, vaultId)}/sketches", request, ct);

    public Task<SketchSummaryDto> UpdateSketchAsync(Guid projectId, Guid vaultId, Guid sketchId, UpdateSketchRequest request,
        CancellationToken ct = default) =>
        api.SendAsync<SketchSummaryDto>(HttpMethod.Put, $"{Vault(projectId, vaultId)}/sketches/{sketchId}", request, ct);

    public Task DeleteSketchAsync(Guid projectId, Guid vaultId, Guid sketchId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Vault(projectId, vaultId)}/sketches/{sketchId}", null, ct);

    public Task<SoloBinary> ImageAsync(Guid projectId, Guid vaultId, Guid imageId, CancellationToken ct = default) =>
        api.GetBytesAsync($"{Vault(projectId, vaultId)}/images/{imageId}", ct);

    public Task<ImageSummaryDto> UploadImageAsync(Guid projectId, Guid vaultId, string path, byte[] bytes, int width, int height,
        CancellationToken ct = default) =>
        api.SendBytesAsync<ImageSummaryDto>(HttpMethod.Post,
            $"{Vault(projectId, vaultId)}/images?path={Uri.EscapeDataString(path)}&width={width}&height={height}",
            bytes, ContentTypeOf(bytes), ct);

    public Task<ImageSummaryDto> ReplaceImageAsync(Guid projectId, Guid vaultId, Guid imageId, byte[] bytes, int width, int height,
        CancellationToken ct = default) =>
        api.SendBytesAsync<ImageSummaryDto>(HttpMethod.Put,
            $"{Vault(projectId, vaultId)}/images/{imageId}?width={width}&height={height}", bytes, ContentTypeOf(bytes), ct);

    /// <summary>What the bytes are; the server sniffs them again and trusts only that.</summary>
    private static string ContentTypeOf(byte[] bytes) => bytes switch
    {
        [0x89, 0x50, ..] => "image/png",
        [0xff, 0xd8, ..] => "image/jpeg",
        [0x47, 0x49, ..] => "image/gif",
        _ => "image/webp",
    };

    // -- Colors and links ------------------------------------------------------

    public Task<List<MemberColorDto>> ColorsAsync(Guid projectId, CancellationToken ct = default) =>
        api.GetAsync<List<MemberColorDto>>($"{Base}/projects/{projectId}/colors", ct);

    public Task<MemberColorDto> ClaimColorAsync(Guid projectId, CancellationToken ct = default) =>
        api.SendAsync<MemberColorDto>(HttpMethod.Post, $"{Base}/projects/{projectId}/colors", null, ct);

    public Task<NoteLocationDto> LocateAsync(Guid noteId, CancellationToken ct = default) =>
        api.GetAsync<NoteLocationDto>($"{Base}/notes/{noteId}/locate", ct);

    public Task<Dictionary<string, string>> LabelsAsync(IReadOnlyList<string> urls, CancellationToken ct = default) =>
        api.SendAsync<Dictionary<string, string>>(HttpMethod.Post, $"{Base}/links/labels", new LinkLabelsRequest { Urls = urls }, ct);

    public Task<List<TaskLookupDto>> LookupTasksAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        api.SendAsync<List<TaskLookupDto>>(HttpMethod.Post, $"{Base}/tasks/lookup", new TaskLookupRequest { Ids = ids }, ct);

    // -- Core ------------------------------------------------------------------

    public Task<List<ProjectSummaryDto>> ProjectsAsync(CancellationToken ct = default) =>
        api.GetAsync<List<ProjectSummaryDto>>("api/projects", ct);

    public async Task<ProjectSummaryDto?> ProjectAsync(string alias, CancellationToken ct = default) =>
        (await ProjectsAsync(ct)).FirstOrDefault(p => string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase));

    public Task<UserDto> MeAsync(CancellationToken ct = default) => api.GetAsync<UserDto>("api/users/me", ct);

    public Task<List<ProjectMemberDto>> MembersAsync(Guid projectId, CancellationToken ct = default) =>
        api.GetAsync<List<ProjectMemberDto>>($"api/projects/{projectId}/members", ct);
}
