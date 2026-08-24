using System.Text.Json;
using Microsoft.JSInterop;

namespace Solo.Client.State;

/// <summary>
/// Remembers which projects the user opened, most-recent first, in
/// <c>localStorage</c>. The sidebar uses this only to auto-expand the last few
/// projects on load; its visible order is the user's separate drag preference.
/// </summary>
public sealed class RecentProjectsStore(IJSRuntime js)
{
    private const string StorageKey = "solo.recentProjects";

    /// <summary>Cap the stored list — the tail is noise once it's this long.</summary>
    private const int MaxEntries = 20;

    /// <summary>How many of the most-recent projects the sidebar unfolds automatically.</summary>
    public const int AutoExpandCount = 3;

    /// <summary>Project ids, most-recently opened first.</summary>
    public async Task<IReadOnlyList<Guid>> GetAsync()
    {
        var raw = await js.InvokeAsync<string?>("solo.storage.get", StorageKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Records a project as the most-recently opened one.</summary>
    public async Task RecordAsync(Guid projectId)
    {
        var recent = (await GetAsync()).ToList();
        recent.RemoveAll(id => id == projectId);
        recent.Insert(0, projectId);
        if (recent.Count > MaxEntries)
        {
            recent.RemoveRange(MaxEntries, recent.Count - MaxEntries);
        }

        await js.InvokeVoidAsync("solo.storage.set", StorageKey, JsonSerializer.Serialize(recent));
    }
}
