---
title: theme.json reference
nav_title: theme.json
lede: Each field of the theme manifest, with its values and its default.
---

## Full example

```jsonc
{
  "formatVersion": 1,
  "id": "my-theme",
  "name": "My Theme",
  "description": "One line about the theme.",
  "author": "Your name",

  "stylesheet": "content/theme.css",

  "fonts": [
    { "family": "My Font", "src": "content/fonts/my-font.woff2",
      "weight": "100 900", "style": "normal", "display": "swap" }
  ],

  "typography": {
    "fontFamily": ["My Font", "Segoe UI", "system-ui", "sans-serif"],
    "headingFontFamily": ["Georgia", "serif"],
    "fontSize": "0.875rem",
    "headingWeight": "700",
    "buttonWeight": "600",
    "letterSpacing": "0.01em"
  },

  "layout": {
    "borderRadius": "8px",
    "appbarHeight": "5em",
    "drawerWidth": "20em"
  },

  "login": {
    "background": {
      "image": "content/login.jpg",
      "imageLight": "content/login-light.jpg",
      "fit": "cover",
      "position": "center",
      "scrim": 0.35,
      "color": "#101010"
    },
    "logo": "content/logo.svg",
    "logoLight": "content/logo-light.svg"
  },

  "dark":  { },
  "light": { }
}
```

A theme needs only `id` and `name`. All other fields are optional. An omitted palette, typography or layout field keeps the default of the UI library.

## Top-level fields

| Field | Content |
|---|---|
| `formatVersion` | The version of the manifest format. The only value is `1`, which is also the default. |
| `id` | The ID of the theme: lower-case letters, digits and `-`. Two themes on one instance cannot have the same ID. |
| `name` | The name in the theme list of the Admin settings. |
| `description` | One line of text below the name. |
| `author` | The author of the theme. |
| `stylesheet` | The path of a CSS file in the package. See [Fonts and stylesheet](fonts-and-css.md#stylesheet). |
| `fonts` | Font files in the package. See [Fonts and stylesheet](fonts-and-css.md#fonts). |
| `typography` | See [Typography](#typography). |
| `layout` | See [Layout](#layout). |
| `login` | See [Sign-in screen](#sign-in-screen). |
| `dark`, `light` | The two palettes. See [Palette](#palette). |

All paths are relative to the root of the package, for example `content/theme.css`.

## Typography

| Field | Content | Default |
|---|---|---|
| `fontFamily` | A list of font families for body text, in order of preference. | The font of the UI library |
| `headingFontFamily` | A list of font families for headings. | The value of `fontFamily` |
| `fontSize` | The base font size, as a CSS length. | `0.875rem` |
| `headingWeight` | The font weight of headings. | `700` |
| `buttonWeight` | The font weight of button text. | `500` |
| `letterSpacing` | The letter spacing of body text, as a CSS length. | None |

> Write font family names without quotation marks: `"Segoe UI"`, not `"'Segoe UI'"`. Solo adds the quotation marks in the CSS. A second pair makes the full `font-family` rule invalid.
{: .warning}

## Layout

| Field | Content | Value in the default theme |
|---|---|---|
| `borderRadius` | The corner radius of cards, buttons and fields. | `8px` |
| `appbarHeight` | The height of the app bar. | `5em` |
| `drawerWidth` | The width of the sidebar. | `20em` |

## Sign-in screen

### Background

| Field | Content | Default |
|---|---|---|
| `image` | The path of the background image. | No image |
| `imageLight` | The path of a different image for the light scheme. | The value of `image` |
| `fit` | How the image fills the screen. See the table below. | `cover` |
| `position` | The position of the image, as a CSS `background-position` value. | `center` |
| `scrim` | A number from `0` to `1`. Solo puts a layer of the background colour on the image, with this opacity. | `0` |
| `color` | The colour behind the image. | The background colour of the palette |

| `fit` | Result |
|---|---|
| `cover` | The image fills the screen. Solo cuts the parts that do not fit. Use this for photographs. |
| `contain` | The full image is visible. Empty bands can show at two sides. |
| `width` | The image is as wide as the screen. The height follows the proportions. |
| `height` | The image is as high as the screen. The width follows the proportions. |
| `stretch` | The image fills the screen in the two directions. The proportions change. |

Use `scrim` with a detailed image. Without it, the text on the sign-in card can be hard to read.

### Logo

| Field | Content |
|---|---|
| `logo` | The path of an image that replaces the word "Solo" on the sign-in card. |
| `logoLight` | The path of a different logo for the light scheme. |

## Palette

`dark` and `light` have the same fields. Each field is optional.

### Colour formats

A colour can be:

- A hex value: `#rgb`, `#rgba`, `#rrggbb` or `#rrggbbaa`.
- A function: `rgb()`, `rgba()`, `hsl()` or `hsla()`.
- A CSS colour keyword, such as `white`.

Solo refuses a package that has a different value in a colour field.

### Brand colours

| Field | Use |
|---|---|
| `primary` | The main accent: buttons, links, the selected sidebar entry and icons. |
| `secondary` | The second accent. |
| `tertiary` | The third accent. |

### Surfaces

| Field | Use |
|---|---|
| `background` | The background of the page. |
| `backgroundGray` | A second background shade. |
| `surface` | The background of cards, dialogs and menus. |
| `appbarBackground` | The background of the app bar. |
| `appbarText` | The text and icons in the app bar. |
| `drawerBackground` | The background of the sidebar. |
| `drawerText` | The text in the sidebar. |
| `drawerIcon` | The icons in the sidebar. |

### Text and controls

| Field | Use |
|---|---|
| `textPrimary` | Body text. |
| `textSecondary` | Captions and less important text. |
| `textDisabled` | The text of disabled controls. |
| `actionDefault` | Icon buttons in their normal state. |
| `actionDisabled` | Disabled controls. |
| `actionDisabledBackground` | The background of disabled controls. |

### Lines and tables

| Field | Use |
|---|---|
| `divider` | Dividers. |
| `dividerLight` | Lighter dividers. |
| `linesDefault` | The borders of cards and panels. |
| `linesInputs` | The borders of text fields. |
| `tableLines` | The lines between table rows. |
| `tableStriped` | The background of alternate table rows. |
| `tableHover` | The background of a table row below the pointer. |

### Status colours

| Field | Use |
|---|---|
| `success` | Success messages and completed items. |
| `warning` | Warnings. |
| `error` | Errors and destructive commands. |
| `info` | Information messages. |

### Overlays

| Field | Use |
|---|---|
| `overlayDark` | The layer behind a dialog. |
| `overlayLight` | A lighter overlay layer. |

### Contrast text

These fields set the text colour on a filled control:

`primaryContrastText`, `secondaryContrastText`, `tertiaryContrastText`, `successContrastText`, `warningContrastText`, `errorContrastText`, `infoContrastText`

Omit them. Solo then calculates each one from the WCAG contrast of its fill colour. For example, a light accent colour gets black text on its buttons.

## The default theme as an example

This is the dark palette of Ahlquist Interactive. It shows colours with transparency for lines:

```json
"dark": {
  "primary": "#fff600",
  "secondary": "#c9c400",
  "tertiary": "#8a8600",
  "background": "#000000",
  "backgroundGray": "#080808",
  "surface": "#101010",
  "appbarBackground": "#000000",
  "appbarText": "#fff600",
  "drawerBackground": "#080808",
  "drawerText": "#ffffff",
  "drawerIcon": "#fff600",
  "textPrimary": "#ffffff",
  "textSecondary": "#b0b0a0",
  "textDisabled": "#6f6f64",
  "actionDefault": "#c9c400",
  "divider": "#fff60040",
  "dividerLight": "#fff60026",
  "linesDefault": "#fff60040",
  "linesInputs": "#fff60059",
  "tableLines": "#fff60026",
  "tableHover": "#fff6000f",
  "success": "#5ad469",
  "warning": "#ffb300",
  "error": "#ff5252",
  "info": "#fff600"
}
```

The full file is [`ahlquist-interactive/theme.json`](https://github.com/HunterAhlquist/Solo/blob/main/src/Solo.Api/Themes/ahlquist-interactive/theme.json).
