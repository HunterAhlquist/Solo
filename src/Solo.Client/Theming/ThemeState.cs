using System.Text.Json;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Shared.Dtos;

namespace Solo.Client.Theming;

/// <summary>
/// The live theme: the instance's theme package, and this user's light/dark
/// choice on this device.
/// </summary>
/// <remarks>
/// <para>The two halves are deliberately stored in different places. The theme
/// is site-wide and lives in the database, because it is the operator's
/// branding decision. The colour scheme lives in <c>localStorage</c>, because it
/// is a per-person, per-device comfort setting — and because the sign-in screen
/// has to honour it before there is a signed-in user to look preferences up
/// for.</para>
/// </remarks>
public sealed class ThemeState(IJSRuntime js)
{
    private const string PreferenceKey = "solo.colorScheme";

    /// <summary>Splash colours cached for <c>js/theme-boot.js</c>; see <see cref="CacheBootStyleAsync"/>.</summary>
    private const string BootStyleKey = "solo.bootStyle";

    private ThemeDto? _theme;

    /// <summary>One-shot latches for the missing-theme report; see <see cref="TryClaimFallbackNotice"/>.</summary>
    private bool _consoleReported;
    private bool _noticeShown;

    /// <summary>The instance's theme, or null when the API couldn't be reached.</summary>
    public ThemeDto? Theme => _theme;

    public MudTheme MudTheme { get; private set; } = MudThemeFactory.Fallback;

    public ColorSchemePreference Preference { get; private set; } = ColorSchemePreference.System;

    /// <summary>The browser's <c>prefers-color-scheme</c>, kept current by the theme provider.</summary>
    public bool SystemPrefersDark { get; private set; } = true;

    public bool IsDarkMode => Preference switch
    {
        ColorSchemePreference.Light => false,
        ColorSchemePreference.Dark => true,
        _ => SystemPrefersDark,
    };

    /// <summary>Raised when the theme or the effective scheme changes; layouts re-render on it.</summary>
    public event Action? Changed;

    /// <summary>The login background for the scheme in force, or null if the theme has none.</summary>
    public ThemeBackgroundDto? LoginBackground
    {
        get
        {
            var background = _theme?.Login?.Background;
            if (background is null)
            {
                return null;
            }

            // A theme may ship one image for both schemes or one per scheme.
            var image = IsDarkMode ? background.Image : background.ImageLight ?? background.Image;
            return image is null ? null : background with { Image = image };
        }
    }

    /// <summary>The sign-in wordmark for the scheme in force, if the theme supplies one.</summary>
    public string? LoginLogo =>
        IsDarkMode ? _theme?.Login?.Logo : _theme?.Login?.LogoLight ?? _theme?.Login?.Logo;

    /// <summary>
    /// The instance's chosen theme is no longer installed and the default is
    /// standing in for it.
    /// </summary>
    public bool IsFallback => _theme?.IsFallback == true;

    /// <summary>The missing theme's id when <see cref="IsFallback"/> is set.</summary>
    public string? MissingThemeId => _theme?.RequestedThemeId;

    /// <summary>
    /// One-line description of the fallback, for the admin alert and the
    /// browser console.
    /// </summary>
    public string FallbackMessage =>
        $"Theme '{MissingThemeId}' is selected for this instance but is not installed. "
        + $"Showing '{_theme?.Name}' instead.";

    /// <summary>
    /// Writes the fallback to the browser console for anyone who opens it —
    /// including users who can't fix it, for whom an alert would only be noise.
    /// The people who <em>can</em> fix it get told properly, by
    /// <see cref="TryClaimFallbackNotice"/> and on the admin settings page.
    /// </summary>
    public async Task ReportFallbackAsync()
    {
        if (!IsFallback || _consoleReported)
        {
            return;
        }

        _consoleReported = true;
        try
        {
            await js.InvokeVoidAsync("console.error", $"[Solo] {FallbackMessage}");
        }
        catch (JSException)
        {
            // Reporting a problem must not become a second problem.
        }
    }

    /// <summary>
    /// True the first time it is asked, for a caller that wants to announce the
    /// fallback once — a snackbar, say.
    /// </summary>
    /// <remarks>
    /// The guard lives here rather than in the caller because a layout is
    /// instantiated several times while the router settles on a route, so
    /// <c>firstRender</c> fires more than once per page load and is not the
    /// "once" it looks like. This state outlives all of them.
    /// </remarks>
    public bool TryClaimFallbackNotice()
    {
        if (!IsFallback || _noticeShown)
        {
            return false;
        }

        return _noticeShown = true;
    }

    public void SetTheme(ThemeDto? theme)
    {
        _theme = theme;
        MudTheme = theme is null ? MudThemeFactory.Fallback : MudThemeFactory.Create(theme);
        Changed?.Invoke();
    }

    /// <summary>
    /// Reads the stored preference and the browser's current scheme.
    ///
    /// <para>Synchronous on purpose. WASM's <see cref="IJSInProcessRuntime"/> can
    /// answer both without yielding, so this runs during
    /// <c>OnInitialized</c> and the very first render is already in the right
    /// scheme — an awaited read would paint one frame of the default first.
    /// Anything unparseable falls back to System.</para>
    /// </summary>
    public void LoadPreference()
    {
        if (js is not IJSInProcessRuntime inProcess)
        {
            return;
        }

        try
        {
            var raw = inProcess.Invoke<string?>("solo.storage.get", PreferenceKey);
            if (Enum.TryParse<ColorSchemePreference>(raw, ignoreCase: true, out var preference))
            {
                Preference = preference;
            }

            SystemPrefersDark = inProcess.Invoke<bool>("solo.systemPrefersDark");
        }
        catch (JSException)
        {
            // Storage disabled (private mode, locked-down browser). System it is.
        }

        try
        {
            inProcess.InvokeVoid("solo.theme.setSource", ThemeSource(Preference));
            inProcess.InvokeVoid("solo.theme.setResolvedScheme", IsDarkMode);
        }
        catch (JSException)
        {
            // The browser theme still works if optional desktop coordination fails.
        }
    }

    public async Task SetPreferenceAsync(ColorSchemePreference preference)
    {
        if (Preference == preference)
        {
            return;
        }

        Preference = preference;

        try
        {
            await js.InvokeVoidAsync("solo.theme.setSource", ThemeSource(preference));
            await js.InvokeVoidAsync("solo.theme.setResolvedScheme", IsDarkMode);
        }
        catch (JSException)
        {
            // Theme rendering is local; native coordination is an enhancement.
        }

        Changed?.Invoke();

        try
        {
            await js.InvokeVoidAsync("solo.storage.set", PreferenceKey, preference.ToString());
        }
        catch (JSException)
        {
            // Not worth surfacing: the choice still applies for this session.
        }

        await CacheBootStyleAsync();
    }

    /// <summary>Called by the theme provider with the browser's current preference.</summary>
    public async Task SetSystemPreferenceAsync(bool prefersDark)
    {
        if (SystemPrefersDark == prefersDark)
        {
            return;
        }

        SystemPrefersDark = prefersDark;
        if (Preference == ColorSchemePreference.System)
        {
            try
            {
                await js.InvokeVoidAsync("solo.theme.setResolvedScheme", IsDarkMode);
            }
            catch (JSException)
            {
                // MudBlazor still re-renders in the right scheme below.
            }

            Changed?.Invoke();
        }
    }

    private static string ThemeSource(ColorSchemePreference preference) => preference switch
    {
        ColorSchemePreference.Light => "light",
        ColorSchemePreference.Dark => "dark",
        _ => "system",
    };

    /// <summary>
    /// Stashes the resolved splash colours so the next cold boot paints in this
    /// theme rather than flashing the hard-coded default before Blazor starts.
    /// The splash renders from <c>index.html</c>, long before the API answers, so
    /// the only thing it can read is what the previous visit left behind.
    /// </summary>
    public async Task CacheBootStyleAsync()
    {
        if (_theme is null)
        {
            return;
        }

        var style = new
        {
            dark = Colors(_theme.Dark, "#000000", "#fff600"),
            light = Colors(_theme.Light, "#fdf9f3", "#df6610"),
        };

        try
        {
            await js.InvokeVoidAsync("solo.storage.set", BootStyleKey, JsonSerializer.Serialize(style));
        }
        catch (JSException)
        {
            // Cosmetic only — a flash on next boot is the whole cost.
        }

        static object Colors(ThemePaletteDto? palette, string background, string foreground) => new
        {
            bg = palette?.Background ?? background,
            fg = palette?.Primary ?? foreground,
            surface = palette?.Surface ?? background,
        };
    }
}
