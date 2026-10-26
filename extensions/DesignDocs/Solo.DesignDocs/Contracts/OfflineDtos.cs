namespace Solo.DesignDocs.Contracts;

public sealed record OfflineManifest(TreeDto Tree, IReadOnlyList<SketchDto> Sketches, IReadOnlyList<ImageDto> Images,
    IReadOnlyDictionary<Guid,string> Documents, bool CanWrite, bool CanManage, bool ProjectWrite = false);
public sealed record OfflineDocument(Guid Id, string Seq, IReadOnlyList<byte[]> Updates, string? SeedMarkdown, int UpdateCount = 0);
public sealed record OfflineWriteResult(string Seq, string PreviousSeq, uint? PreviousVersion, uint? Version);
public sealed record OfflineWrite(Guid Id, byte[] Data, string? SeedMarkdown = null);
public sealed record OfflineCommand(Guid Id, string Entity, string Action, Guid TargetId, uint Version = 0,
    string? Name = null, Guid? ParentId = null, Guid? NoteId = null, string? PageSize = null,
    string? Orientation = null, string? Pattern = null, bool IsClosed = false, byte[]? Data = null,
    string? BaseName = null, Guid? BaseParentId = null,
    IReadOnlyDictionary<Guid,string>? BaseDocuments = null, IReadOnlyDictionary<Guid,uint>? BaseVersions = null);

public sealed record OfflineCompaction(string ThroughSeq, byte[] Data);
