using MudBlazor;
using MudBlazor.Utilities;
using Solo.Shared.Dtos;

namespace Solo.Client.Theming;

/// <summary>
/// Turns a theme package into the <see cref="MudTheme"/> the providers render.
/// </summary>
/// <remarks>
/// Every value arrives already validated by the API's package loader — colours
/// are checked and contrast text derived at import time — so this is a straight
/// projection. Anything the theme leaves null keeps MudBlazor's own default for
/// that scheme, which is what lets a minimal theme set a handful of colours and
/// still render coherently.
/// </remarks>
public static class MudThemeFactory
{
    /// <summary>
    /// Used before the API answers, and on the boot-failure screen when it never
    /// does. Deliberately just enough to render a legible error on a dark
    /// surface — the real Ahlquist Interactive theme is a package like any other
    /// and cannot be read when the instance is unreachable.
    /// </summary>
    public static readonly MudTheme Fallback = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#fff600",
            PrimaryContrastText = "#000000",
            Background = "#000000",
            Surface = "#101010",
            AppbarBackground = "#000000",
            AppbarText = "#fff600",
            TextPrimary = "#ffffff",
            TextSecondary = "#b0b0a0",
        },
        Typography = MonospaceTypography(),
    };

    public static MudTheme Create(ThemeDto theme)
    {
        var mud = new MudTheme();

        if (theme.Dark is { } dark)
        {
            Apply(mud.PaletteDark, dark);
        }

        if (theme.Light is { } light)
        {
            Apply(mud.PaletteLight, light);
        }

        if (theme.Layout is { } layout)
        {
            mud.LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = layout.BorderRadius ?? mud.LayoutProperties.DefaultBorderRadius,
                AppbarHeight = layout.AppbarHeight ?? mud.LayoutProperties.AppbarHeight,
                DrawerWidthLeft = layout.DrawerWidth ?? mud.LayoutProperties.DrawerWidthLeft,
            };
        }

        if (theme.Typography is { } typography)
        {
            mud.Typography = Build(typography);
        }

        return mud;
    }

    private static void Apply(Palette palette, ThemePaletteDto values)
    {
        // MudBlazor's palette properties are MudColor, which converts from a CSS
        // string; a null here means "keep the scheme default", so every
        // assignment is guarded rather than blanket-copied.
        Set(v => palette.Primary = v, values.Primary);
        Set(v => palette.Secondary = v, values.Secondary);
        Set(v => palette.Tertiary = v, values.Tertiary);

        Set(v => palette.PrimaryContrastText = v, values.PrimaryContrastText);
        Set(v => palette.SecondaryContrastText = v, values.SecondaryContrastText);
        Set(v => palette.TertiaryContrastText = v, values.TertiaryContrastText);
        Set(v => palette.SuccessContrastText = v, values.SuccessContrastText);
        Set(v => palette.WarningContrastText = v, values.WarningContrastText);
        Set(v => palette.ErrorContrastText = v, values.ErrorContrastText);
        Set(v => palette.InfoContrastText = v, values.InfoContrastText);

        Set(v => palette.Background = v, values.Background);
        Set(v => palette.BackgroundGray = v, values.BackgroundGray);
        Set(v => palette.Surface = v, values.Surface);

        Set(v => palette.AppbarBackground = v, values.AppbarBackground);
        Set(v => palette.AppbarText = v, values.AppbarText);
        Set(v => palette.DrawerBackground = v, values.DrawerBackground);
        Set(v => palette.DrawerText = v, values.DrawerText);
        Set(v => palette.DrawerIcon = v, values.DrawerIcon);

        Set(v => palette.TextPrimary = v, values.TextPrimary);
        Set(v => palette.TextSecondary = v, values.TextSecondary);
        Set(v => palette.TextDisabled = v, values.TextDisabled);

        Set(v => palette.ActionDefault = v, values.ActionDefault);
        Set(v => palette.ActionDisabled = v, values.ActionDisabled);
        Set(v => palette.ActionDisabledBackground = v, values.ActionDisabledBackground);

        Set(v => palette.Divider = v, values.Divider);
        Set(v => palette.DividerLight = v, values.DividerLight);
        Set(v => palette.LinesDefault = v, values.LinesDefault);
        Set(v => palette.LinesInputs = v, values.LinesInputs);
        Set(v => palette.TableLines = v, values.TableLines);
        Set(v => palette.TableStriped = v, values.TableStriped);
        Set(v => palette.TableHover = v, values.TableHover);

        Set(v => palette.Success = v, values.Success);
        Set(v => palette.Warning = v, values.Warning);
        Set(v => palette.Error = v, values.Error);
        Set(v => palette.Info = v, values.Info);

        // The overlays are plain strings on Palette, not MudColor.
        SetRaw(v => palette.OverlayDark = v, values.OverlayDark);
        SetRaw(v => palette.OverlayLight = v, values.OverlayLight);

        static void Set(Action<MudColor> assign, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                assign(new MudColor(value));
            }
        }

        static void SetRaw(Action<string> assign, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                assign(value);
            }
        }
    }

    /// <summary>
    /// Each MudBlazor typography variant carries its own font-family, so a stack
    /// set only on Default leaves every heading on Roboto. Themes name at most
    /// two stacks — body and headings — and they are applied across the board.
    /// </summary>
    private static Typography Build(ThemeTypographyDto t)
    {
        var body = t.FontFamily?.ToArray();
        var heading = t.HeadingFontFamily?.ToArray() ?? body;
        var headingWeight = t.HeadingWeight ?? "700";
        var buttonWeight = t.ButtonWeight ?? "500";
        var spacing = t.LetterSpacing;

        var typography = new Typography
        {
            Default = new DefaultTypography { FontSize = t.FontSize ?? "0.875rem" },
            H1 = new H1Typography { FontSize = "1.75rem", FontWeight = headingWeight },
            H2 = new H2Typography { FontSize = "1.5rem", FontWeight = headingWeight },
            H3 = new H3Typography { FontSize = "1.25rem", FontWeight = headingWeight },
            H4 = new H4Typography { FontSize = "1.1rem", FontWeight = headingWeight },
            H5 = new H5Typography { FontSize = "1rem", FontWeight = headingWeight },
            H6 = new H6Typography { FontSize = "0.9rem", FontWeight = headingWeight },
            Body1 = new Body1Typography(),
            Body2 = new Body2Typography(),
            Subtitle1 = new Subtitle1Typography(),
            Subtitle2 = new Subtitle2Typography(),
            Button = new ButtonTypography { FontWeight = buttonWeight },
            Caption = new CaptionTypography(),
            Overline = new OverlineTypography(),
        };

        if (body is not null)
        {
            foreach (var variant in new BaseTypography[]
            {
                typography.Default, typography.Body1, typography.Body2, typography.Subtitle1,
                typography.Subtitle2, typography.Button, typography.Caption, typography.Overline,
            })
            {
                variant.FontFamily = body;
            }
        }

        if (heading is not null)
        {
            foreach (var variant in new BaseTypography[]
            {
                typography.H1, typography.H2, typography.H3, typography.H4, typography.H5, typography.H6,
            })
            {
                variant.FontFamily = heading;
            }
        }

        if (!string.IsNullOrWhiteSpace(spacing))
        {
            typography.Default.LetterSpacing = spacing;
            typography.Body1.LetterSpacing = spacing;
            typography.Body2.LetterSpacing = spacing;
        }

        return typography;
    }

    private static Typography MonospaceTypography()
    {
        // Unquoted: MudBlazor quotes any family containing a space when it emits
        // the CSS variable, and quoting here would double it into invalid CSS.
        string[] mono = ["Courier New", "monospace"];
        return new Typography
        {
            Default = new DefaultTypography { FontFamily = mono, FontSize = "0.875rem" },
            H1 = new H1Typography { FontFamily = mono, FontSize = "1.75rem", FontWeight = "700" },
            Body1 = new Body1Typography { FontFamily = mono },
            Body2 = new Body2Typography { FontFamily = mono },
            Button = new ButtonTypography { FontFamily = mono, FontWeight = "700" },
        };
    }
}
