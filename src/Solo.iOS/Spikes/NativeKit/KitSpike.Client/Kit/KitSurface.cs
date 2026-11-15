namespace KitSpike.Client.Kit;

/// <summary>
/// Where kit components are being drawn. Cascaded by a host that wants them as
/// a tree of kit nodes rather than as the web's own markup; with none in
/// scope, a kit component draws itself for the web.
/// </summary>
public sealed class KitSurface(Func<string, Task> open)
{
    /// <summary>Asks the surface to go to one of the app's routes, which is the surface's to do.</summary>
    public Task OpenAsync(string route) => open(route);
}

/// <summary>The names a kit icon may have, and the web's drawing of each.</summary>
public static class KitIcons
{
    public static string Material(string? name) => name switch
    {
        "Timeline" => MudBlazor.Icons.Material.Filled.Timeline,
        "Flag" => MudBlazor.Icons.Material.Filled.Flag,
        _ => MudBlazor.Icons.Material.Filled.Extension,
    };
}
