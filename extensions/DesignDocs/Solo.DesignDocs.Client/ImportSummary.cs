namespace Solo.DesignDocs.Client;
public sealed record ImportSummary(int Notes,int Images,IReadOnlyList<string> Skipped,IReadOnlyList<string> Conflicts,int Videos=0);
