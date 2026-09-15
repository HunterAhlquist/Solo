using Microsoft.Extensions.Options;
using Solo.Shared.Dtos;

namespace Solo.Api.Theming;

public sealed class ThemeOptions
{
    public const string Section = "Themes";

    /// <summary>
    /// Directory scanned for <c>*.zip</c> theme packages — a mounted volume in a
    /// deployment, so themes survive an image update the way the database does.
    /// Missing or empty means built-in themes only.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Theme used when the instance hasn't chosen one, or chose one that isn't installed.</summary>
    public string DefaultThemeId { get; set; } = "ahlquist-interactive";
}
