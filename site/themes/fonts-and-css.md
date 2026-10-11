---
title: Fonts and stylesheet
lede: Add font files to a theme, and use a stylesheet for the details that theme.json cannot set.
---

## Fonts

To use your own font, do these three steps:

1. Put the font files in the package, for example in `content/fonts/`.
2. Declare each file in `fonts`.
3. Name the font family in `typography`.

```jsonc
"fonts": [
  { "family": "Inter", "src": "content/fonts/inter-variable.woff2", "weight": "100 900" },
  { "family": "Inter", "src": "content/fonts/inter-italic.woff2", "weight": "100 900", "style": "italic" }
],
"typography": {
  "fontFamily": ["Inter", "system-ui", "sans-serif"]
}
```

Solo writes the `@font-face` rules from this list. You do not write them in your stylesheet.

### Font fields

| Field | Required | Content | Default |
|---|---|---|---|
| `family` | Yes | The name of the font family. Use the same name in `typography`. | |
| `src` | Yes | The path of a `.woff2`, `.woff`, `.ttf` or `.otf` file in the package. | |
| `weight` | No | One weight (`400`), a keyword (`normal`, `bold`), or a range for a variable font (`100 900`). | `normal` |
| `style` | No | `normal`, `italic` or `oblique`. | `normal` |
| `display` | No | `auto`, `block`, `swap`, `fallback` or `optional`. | `swap` |

Write one entry for each file. A family with a regular file and an italic file has two entries with the same `family`.

### Why you declare fonts in the manifest

Solo checks each `src` path when it loads the package. If a path is wrong, Solo refuses the package and gives the reason.

With an `@font-face` rule in your own stylesheet, a wrong path gives no error. The browser uses a different font, and the mistake is hard to see.

### Recommendations

- **Use `.woff2`.** A `.woff2` file is usually about one third of the size of the `.ttf` file. All browsers that Solo supports can read it.
- **Make a subset of a large font.** Keep only the characters that you need. The limit is 12 MB for each file, but the browser of each person must download the file.
- **Keep `display` at `swap`.** The text is then readable in a fallback font while the font loads.
- **Check the licence.** You can put a font with an open licence, such as OFL or Apache, in a package. Many commercial licences do not permit this.

## Stylesheet

Use a stylesheet for the details that the manifest cannot set. Name the file in the manifest:

```json
"stylesheet": "content/theme.css"
```

Solo loads this file after its own stylesheets. Your rules replace the rules of Solo.

Prefer the manifest when it can do the job. Solo checks the manifest, and a value in the manifest cannot break a page. A stylesheet has no such check.

### Use the palette variables

The UI library, MudBlazor, puts each palette colour in a CSS variable. Use the variables, not fixed colours. One rule then works in the two schemes.

```css
.mud-appbar {
    border-bottom: 1px solid var(--mud-palette-divider);
}
```

Variables that are often useful:

| Variable | Palette field |
|---|---|
| `--mud-palette-primary` | `primary` |
| `--mud-palette-secondary` | `secondary` |
| `--mud-palette-background` | `background` |
| `--mud-palette-surface` | `surface` |
| `--mud-palette-text-primary` | `textPrimary` |
| `--mud-palette-text-secondary` | `textSecondary` |
| `--mud-palette-divider` | `divider` |
| `--mud-palette-lines-default` | `linesDefault` |
| `--mud-default-borderradius` | The `borderRadius` of the layout |

Solo adds one variable of its own:

| Variable | Use |
|---|---|
| `--solo-code-color` | The colour of inline code in descriptions and comments. |

### Example: the default theme

This is the full stylesheet of Ahlquist Interactive:

```css
:root {
    /* Inline code uses a pink that is not in the palette. */
    --solo-code-color: #c02d76;
}

/* A thin line below the app bar separates it from a black page. */
.mud-appbar {
    border-bottom: 1px solid var(--mud-palette-divider);
}

/* Headings in capital letters with wide spacing. */
.mud-typography-h1 {
    letter-spacing: 0.18em;
    text-transform: uppercase;
}
```

The stylesheets of [Blueprint](https://github.com/HunterAhlquist/Solo/blob/main/src/Solo.Api/Themes/blueprint/content/theme.css) and [Arcade](https://github.com/HunterAhlquist/Solo/blob/main/src/Solo.Api/Themes/arcade/content/theme.css) do more: a drawing grid and neon effects.

### Refer to files in the package

In a stylesheet, use a path relative to the stylesheet:

```css
.mud-main-content {
    background-image: url("pattern.svg");
}
```

Solo adds a hash of the package to the address of each file and tells the browser to keep the file. When you change a file and install the package again, its address changes. You do not need version numbers in file names.

### Limits of a stylesheet

- A stylesheet depends on the class names of Solo and MudBlazor. A new version of Solo can change them. Test your theme after an update.
- A theme cannot add text, pages or behaviour. For that, write an [extension](../extensions/index.md).
