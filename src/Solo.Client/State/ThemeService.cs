using Microsoft.JSInterop;

namespace Solo.Client.State;

/// <summary>
/// Dark by default — this is a tool for a game-dev audience, not a marketing
/// site. The choice persists in <c>localStorage</c>.
/// </summary>
public sealed class ThemeService(IJSRuntime js)
{
    private const string StorageKey = "solo.theme";

    public bool IsDarkMode { get; private set; } = true;

    public event Action? Changed;

    /// <summary>Reads the stored preference. Call once, after the first render.</summary>
    public async Task InitializeAsync()
    {
        var stored = await js.InvokeAsync<string?>("solo.storage.get", StorageKey);
        IsDarkMode = stored is null || stored == "dark";
        Changed?.Invoke();
    }

    public async Task SetDarkModeAsync(bool isDarkMode)
    {
        if (IsDarkMode == isDarkMode)
        {
            return;
        }

        IsDarkMode = isDarkMode;
        await js.InvokeVoidAsync("solo.storage.set", StorageKey, isDarkMode ? "dark" : "light");
        Changed?.Invoke();
    }

    public Task ToggleAsync() => SetDarkModeAsync(!IsDarkMode);
}
