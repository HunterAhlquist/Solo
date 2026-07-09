using MudBlazor;

namespace Solo.Client.Layout;

/// <summary>
/// Black-and-yellow monospace "terminal" identity, shared with the sibling
/// AI.Website.Mud site: pure-black surfaces, a single high-voltage yellow accent,
/// Courier New throughout.
/// </summary>
/// <remarks>
/// Dark-only: both layouts pin <c>IsDarkMode</c>, so the theme's light palette
/// is unused. It stays defined because MudTheme requires one, not because
/// anything renders it.
/// </remarks>
public static class SoloTheme
{
    // Each MudBlazor typography variant carries its own font-family variable, so a
    // family set only on Default leaves headings on Roboto. Apply it everywhere.
    private static readonly string[] Mono = ["'Courier New'", "monospace"];

    private const string Yellow = "#fff600";

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = Yellow,
            Secondary = "#c9c400",     // dimmed gold for the rare second accent
            Tertiary = "#8a8600",
            // MudBlazor has no automatic contrast detection — the text colour on a
            // filled button is a static *ContrastText per colour, defaulting to
            // white. White on this yellow is unreadable, so set them by relative
            // luminance (the >150 rule LabelChip uses): light fills get black text.
            // Primary/Secondary/Success/Warning/Info are light → black; Tertiary and
            // Error are dark enough to keep MudBlazor's white default.
            PrimaryContrastText = "#000000",
            SecondaryContrastText = "#000000",
            SuccessContrastText = "#000000",
            WarningContrastText = "#000000",
            InfoContrastText = "#000000",
            Background = "#000000",
            BackgroundGray = "#080808",
            Surface = "#101010",
            AppbarBackground = "#000000",
            AppbarText = Yellow,
            DrawerBackground = "#080808",
            DrawerText = "#ffffff",
            DrawerIcon = Yellow,
            TextPrimary = "#ffffff",
            TextSecondary = "#b0b0a0",
            ActionDefault = "#c9c400",
            Divider = "#fff60040",     // translucent yellow hairlines
            LinesDefault = "#fff60040",
            TableLines = "#fff60026",
            // Semantic colours stay meaningful — an error snackbar must read as an
            // error, not as brand yellow — but are tuned to sit on pure black.
            Success = "#5ad469",
            Warning = "#ffb300",
            Error = "#ff5252",
            Info = Yellow,
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#8a8400",       // yellow is invisible on white; darken it
            Secondary = "#6f6b00",
            Tertiary = "#9a7a00",
            Background = "#fafaf5",
            Surface = "#ffffff",
            AppbarBackground = "#0a0a0a",
            AppbarText = Yellow,       // keep the black-and-yellow header even in light mode
            DrawerBackground = "#ffffff",
            TextPrimary = "#1a1a10",
            TextSecondary = "#5c5c50",
            Divider = "#d8d8c8",
            LinesDefault = "#d8d8c8",
            Success = "#2f855a",
            Warning = "#b7791f",
            Error = "#c53030",
            Info = "#6f6b00",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "5em",
            DrawerWidthLeft = "20em",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Mono, FontSize = "0.875rem" },
            H1 = new H1Typography { FontFamily = Mono, FontSize = "1.75rem", FontWeight = "700" },
            H2 = new H2Typography { FontFamily = Mono, FontSize = "1.5rem", FontWeight = "700" },
            H3 = new H3Typography { FontFamily = Mono, FontSize = "1.25rem", FontWeight = "700" },
            H4 = new H4Typography { FontFamily = Mono, FontSize = "1.1rem", FontWeight = "700" },
            H5 = new H5Typography { FontFamily = Mono, FontSize = "1rem", FontWeight = "700" },
            H6 = new H6Typography { FontFamily = Mono, FontSize = "0.9rem", FontWeight = "700" },
            Body1 = new Body1Typography { FontFamily = Mono },
            Body2 = new Body2Typography { FontFamily = Mono },
            Subtitle1 = new Subtitle1Typography { FontFamily = Mono },
            Subtitle2 = new Subtitle2Typography { FontFamily = Mono },
            // Courier New at MudBlazor's default 500 reads thin, especially white-on-
            // yellow on filled buttons; 700 gives the labels the weight the headings have.
            Button = new ButtonTypography { FontFamily = Mono, FontWeight = "700" },
            Caption = new CaptionTypography { FontFamily = Mono },
            Overline = new OverlineTypography { FontFamily = Mono },
        },
    };
}
