namespace Solo.DesignDocs.Client;
public sealed record DocTreeEntry(Guid Id, bool IsFolder, Guid? ParentId, string Name, uint Version);
public sealed record DocTreeMenu(DocTreeEntry Entry,double X,double Y);

public sealed record DocTreeDrop(Guid Id, Guid? ParentId);

public sealed record DocTreeExpansion(Guid Id, bool Expanded);
