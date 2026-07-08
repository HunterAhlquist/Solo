using MudBlazor;

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
}
