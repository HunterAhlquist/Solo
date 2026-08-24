using System.Text.Json;
using Microsoft.JSInterop;

namespace Solo.Client.State;

/// <summary>
/// Persists the user's explicit sidebar project order in <c>localStorage</c>.
/// This is a presentation preference, so it stays local to the browser rather
/// than becoming shared project data.
/// </summary>
public sealed class ProjectOrderStore(IJSRuntime js)
{
    private const string StorageKey = "solo.projectOrder";

    public async Task<IReadOnlyList<Guid>> GetAsync()
    {
        var raw = await js.InvokeAsync<string?>("solo.storage.get", StorageKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<Guid>>(raw) ?? [])
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public Task SetAsync(IEnumerable<Guid> projectIds) =>
        js.InvokeVoidAsync(
            "solo.storage.set",
            StorageKey,
            JsonSerializer.Serialize(projectIds.Distinct())).AsTask();
}
