using MudBlazor;
using Solo.Client.Api;

namespace Solo.Client.Components;

/// <summary>
/// Icons are stored as free-text names in the DTOs. This maps a stored name back
/// to a MudBlazor icon path, and curates the shortlist the picker offers —
/// game-dev flavoured, because that's the audience.
/// </summary>
public static class SoloIcons
{
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Games"] = Icons.Material.Filled.SportsEsports,
        ["Rocket"] = Icons.Material.Filled.RocketLaunch,
        ["Bug"] = Icons.Material.Filled.BugReport,
        ["Brush"] = Icons.Material.Filled.Brush,
        ["Palette"] = Icons.Material.Filled.Palette,
        ["Code"] = Icons.Material.Filled.Code,
        ["Terminal"] = Icons.Material.Filled.Terminal,
        ["Audio"] = Icons.Material.Filled.GraphicEq,
        ["Music"] = Icons.Material.Filled.MusicNote,
        ["Movie"] = Icons.Material.Filled.Movie,
        ["Camera"] = Icons.Material.Filled.CameraAlt,
        ["Animation"] = Icons.Material.Filled.Animation,
        ["Cube"] = Icons.Material.Filled.ViewInAr,
        ["Map"] = Icons.Material.Filled.Map,
        ["Terrain"] = Icons.Material.Filled.Terrain,
        ["Physics"] = Icons.Material.Filled.Science,
        ["Lightbulb"] = Icons.Material.Filled.Lightbulb,
        ["Speed"] = Icons.Material.Filled.Speed,
        ["Build"] = Icons.Material.Filled.Build,
        ["Storage"] = Icons.Material.Filled.Storage,
        ["Cloud"] = Icons.Material.Filled.Cloud,
        ["Shield"] = Icons.Material.Filled.Shield,
        ["Flag"] = Icons.Material.Filled.Flag,
        ["Star"] = Icons.Material.Filled.Star,
        ["Bolt"] = Icons.Material.Filled.Bolt,
        ["Fire"] = Icons.Material.Filled.LocalFireDepartment,
        ["Puzzle"] = Icons.Material.Filled.Extension,
        ["Dashboard"] = Icons.Material.Filled.Dashboard,
        ["Book"] = Icons.Material.Filled.MenuBook,
        ["Chat"] = Icons.Material.Filled.Chat,
        ["People"] = Icons.Material.Filled.People,
        ["Calendar"] = Icons.Material.Filled.CalendarMonth,
    };

    public static IEnumerable<string> Names => ByName.Keys;

    /// <summary>Falls back to a neutral icon so an unknown stored name never renders a blank box.</summary>
    public static string Resolve(string? name) =>
        name is not null && ByName.TryGetValue(name, out var icon)
            ? icon
            : Icons.Material.Filled.Folder;

    /// <summary>
    /// Icon value for a MudBlazor <c>Icon</c> parameter that also honours an uploaded
    /// image. MudBlazor renders the string as the inner content of an
    /// <c>&lt;svg viewBox="0 0 24 24"&gt;</c>, so the image goes in as an SVG
    /// <c>&lt;image&gt;</c> element — not an HTML <c>&lt;img&gt;</c> (MudBlazor
    /// discussion #5737). This is how images reach surfaces that only take a string
    /// icon, like the sidebar tree.
    /// </summary>
    public static string Resolve(Guid? imageId, string? name) =>
        imageId is { } id
            ? $"<image href=\"{ApiClient.ImageUrl(id)}\" x=\"0\" y=\"0\" width=\"24\" height=\"24\" " +
              "preserveAspectRatio=\"xMidYMid slice\" />"
            : Resolve(name);
}
