namespace Solo.DesignDocs.Client;

public sealed record VaultViewPreferences(Guid[] OpenFolders, Guid? NoteId, int FontScale,int MarginWidth=-1);
