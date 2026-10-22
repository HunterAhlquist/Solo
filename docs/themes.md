# Themes

A theme controls Solo's whole look: both colour schemes, typography, layout
metrics, an optional stylesheet, and the sign-in background.

Two settings, deliberately kept apart:

- **Which theme** is site-wide, chosen by the super admin in **Admin settings**,
  and stored in the database. It's the instance's branding.
- **Light or dark** is per person, chosen from the profile menu, and stored in
  that browser's `localStorage`. "Match system" follows `prefers-color-scheme`
  and keeps following it.

Three themes ship in the image: **Ahlquist Interactive** (the black-and-yellow
terminal, and the default), **Blueprint**, and **Arcade**.

## Installing a theme

Theme packages are `.zip` files. Copy one into the instance's themes directory —
`/data/themes` in the deployment template, backed by a Docker volume so it
survives an image update — then open **Admin settings** and press **Rescan**. No
restart.

A package that fails to load is listed there with the reason. The instance keeps
serving the previous set until a scan succeeds, so a half-copied zip can't take
the site down.

### The directory has to be readable by the app

The container runs as a **non-root user**, so a themes directory it cannot read
means no packages load. The app still starts and still renders — built-in themes
only — and names the problem in Admin settings and in the startup log.

- **Named volume** (the compose default): handled. The image creates
  `/data/themes` owned by the app user, and Docker seeds the volume from it.
- **Host path** (`/mnt/tank/apps/solo/themes:/data/themes`): the host's own
  ownership wins, so make the path readable by UID `1654` — or world-readable.
  `Permission denied` in the startup log is this.

Removing a package is the same in reverse. If the active theme disappears, the
instance falls back to the default rather than failing to render.

There is no upload button, on purpose: a theme's stylesheet can restyle anything
on the page, so installing one stays at the same trust level as deploying the
app.

## Package layout

```
my-theme.zip
├── theme.json          ← required, at the root
└── content/            ← everything else lives here
    ├── theme.css
    ├── login.jpg
    ├── login-light.jpg
    └── fonts/
        └── my-font.woff2
```

Allowed content types: `.css`, `.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`, `.svg`,
`.woff`, `.woff2`, `.ttf`, `.otf`. Limits: 12 MB per file, 12 MB per archive,
48 MB expanded, 200 files.

File and folder names are restricted to letters, digits, `.`, `_` and `-` — no
spaces. They end up in URLs and in generated CSS, and this way neither has to
worry about escaping.

The built-in themes under `src/Solo.Api/Themes/` are the same format unzipped —
copy one as a starting point.

## theme.json

```jsonc
{
  "formatVersion": 1,
  "id": "my-theme",              // lower-case, a-z 0-9 -, unique on the instance
  "name": "My Theme",            // shown in the admin picker
  "description": "One line about it.",
  "author": "You",

  "stylesheet": "content/theme.css",

  "fonts": [                                       // optional; see "Packing fonts"
    { "family": "My Font", "src": "content/fonts/my-font.woff2",
      "weight": "100 900", "style": "normal", "display": "swap" }
  ],

  "typography": {
    "fontFamily": ["My Font", "Segoe UI", "system-ui", "sans-serif"],  // unquoted
    "headingFontFamily": ["Georgia", "serif"],     // optional; defaults to fontFamily
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
      "imageLight": "content/login-light.jpg",  // optional per-scheme variant
      "fit": "cover",
      "position": "center",
      "scrim": 0.35,
      "color": "#101010"
    },
    "logo": "content/logo.svg",                 // optional; replaces the "Solo" wordmark
    "logoLight": "content/logo-light.svg"
  },

  "dark":  { /* palette */ },
  "light": { /* palette */ }
}
```

Every field except `id` and `name` is optional. Anything omitted keeps
MudBlazor's default for that scheme, so a theme can set six colours and still
render coherently.

**Write font family names unquoted.** MudBlazor adds the quotes when it emits
the CSS, so `"'Segoe UI'"` would become `''Segoe UI''` — invalid, and it takes
the whole `font-family` declaration down with it silently. Quotes are stripped
on import if you write them anyway.

### Background fit

| `fit` | Behaviour |
| --- | --- |
| `cover` | Fill the viewport, cropping the overflowing axis. Default; right for photos. |
| `contain` | Fit the whole image in view, letterboxing the short axis. |
| `width` | Match the viewport width; height follows the aspect ratio. |
| `height` | Match the viewport height; width follows the aspect ratio. |
| `stretch` | Match both axes, distorting the aspect ratio. |

`scrim` (0–1) lays a flat wash of the palette background over the image. Use it
— a busy photo behind the sign-in card is the fastest way to make it unreadable.

The desktop app's sign-in page (`/desktop-auth`, served by the API rather than
the client) wears the same login background and logo, plus the palette,
fonts and corner radius. It picks the palette from the system's
`prefers-color-scheme`, since the app's own scheme choice is stored on another
origin, and it does not load the theme's stylesheet, whose selectors target
the client's components.

### Palette

Both `dark` and `light` take the same keys. Supply both: a theme that defines
only one leaves the other scheme on MudBlazor's stock palette.

```jsonc
{
  "primary": "#58c7e8", "secondary": "#7f9cf5", "tertiary": "#2c7fa5",
  "background": "#071019", "backgroundGray": "#0a1520", "surface": "#0e1c2a",
  "appbarBackground": "#061019", "appbarText": "#9fdcf2",
  "drawerBackground": "#0a1520", "drawerText": "#dbe9f2", "drawerIcon": "#58c7e8",
  "textPrimary": "#dbe9f2", "textSecondary": "#90a9bd", "textDisabled": "#5d7183",
  "actionDefault": "#90a9bd", "actionDisabled": "…", "actionDisabledBackground": "…",
  "divider": "#1c3d55", "dividerLight": "#16344a",
  "linesDefault": "#1c3d55", "linesInputs": "#2a5773",
  "tableLines": "#16344a", "tableStriped": "…", "tableHover": "#58c7e80f",
  "success": "#3fbf87", "warning": "#e0a33e", "error": "#ef6b6b", "info": "#58c7e8",
  "overlayDark": "…", "overlayLight": "…"
}
```

Colours may be `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, `rgb()`, `rgba()`,
`hsl()`, `hsla()`, or a CSS colour keyword. Anything else is refused — palette
values are emitted as CSS custom-property values, and a stray `}` would break
the block.

**Don't set the `*ContrastText` keys** (`primaryContrastText`, and the same for
secondary/tertiary/success/warning/error/info) unless you specifically want
something other than the readable answer. Left out, each is derived from its
fill's WCAG contrast, so a light accent gets black button labels instead of
MudBlazor's default white-on-light.

### Contrast

`ThemeContrastTests` asserts WCAG AA across every built-in theme in both
schemes: 4.5:1 for text on the three surfaces, 3:1 for accents and app-bar text,
4.5:1 for labels on filled controls. It's worth running the same numbers over a
theme of your own — a palette is easy to get wrong in a way that only shows up
for someone else.

## Packing fonts

Drop the font files in the package, declare them in `fonts`, and name the family
in `typography`. Nothing else is needed — the API generates the `@font-face`
rules and the client loads them.

```jsonc
"fonts": [
  { "family": "Inter", "src": "content/fonts/inter-variable.woff2", "weight": "100 900" },
  { "family": "Inter", "src": "content/fonts/inter-italic.woff2", "weight": "100 900", "style": "italic" }
],
"typography": { "fontFamily": ["Inter", "system-ui", "sans-serif"] }
```

| Field | Notes |
| --- | --- |
| `family` | Required. The name `typography` then refers to. |
| `src` | Required. Package-relative path to a `.woff2`, `.woff`, `.ttf` or `.otf`. |
| `weight` | A weight (`400`), `normal`/`bold`, or a variable range (`100 900`). Default `normal`. |
| `style` | `normal`, `italic` or `oblique`. Default `normal`. |
| `display` | `auto`, `block`, `swap`, `fallback`, `optional`. Default `swap`. |

One entry per file: a family with regular and italic files needs two entries
sharing a `family`, as above.

Declaring fonts here rather than hand-writing `@font-face` in your stylesheet is
worth it for one reason: the path is checked when the package is imported. A
typo is a refused package with a message, instead of a silent fallback to a
system font that you notice weeks later.

Prefer `.woff2` — it's typically a third the size of the `.ttf` it came from,
and every browser Solo supports reads it. Subset the font to the characters you
need if it's large; the 12 MB per-file cap is generous but the download isn't
free. `font-display: swap` is the default so the app is readable in a fallback
face while the font arrives, rather than blank.

Check the licence before shipping a font. Open-licensed families (OFL, Apache)
are fine to redistribute inside a package; many commercial licences are not.

## Custom stylesheet

`stylesheet` is loaded last, after `app.css` and MudBlazor's own, so it wins.
It's for what the palette can't express — see `content/theme.css` in Blueprint
(the drafting grid) and Arcade (the neon).

Reach for the MudBlazor variables rather than literals, so one rule works in
both schemes:

```css
.mud-appbar { border-bottom: 1px solid var(--mud-palette-divider); }
```

Solo adds `--solo-code-color` for inline `<code>`.

Two things worth knowing:

- Content URLs carry a hash of the package and are served `immutable`. Change a
  file, and its URL changes — there is no stale-cache problem, and no need to
  version filenames.
- Anything the stylesheet can't do, the JSON probably can. Prefer the JSON:
  it's validated, and it can't break the app.
