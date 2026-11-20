# Solo documentation site

The source of the documentation that GitHub Pages publishes. It is a Jekyll
site with its own layout, styled after Solo's default theme, Ahlquist
Interactive.

```
_config.yml          Jekyll settings
_data/nav.yml        the sidebar: sections, page order, icons
_layouts/            default (app bar, sidebar, footer), page, home
_includes/           the icon sprite, and the screenshot include
assets/css/docs.css  the whole design; its tokens are the theme's palette
assets/js/docs.js    drawer, appearance menu, "On this page", copy buttons
assets/models/       the front page's 3D mark, exported from Blender
_hero/               that mark's script and the library it is bundled with
index.md             the front page
guide/               using Solo
admin/               running an instance
extensions/          writing extensions
themes/              writing themes
```

## Preview a change

```bash
build/preview-site.sh
```

This builds the site in a container with the same `github-pages` gem that the
Pages build runs, and serves it at <http://localhost:4000/Solo/>. It needs
Docker, python3 and Node (for the front page's 3D mark, below). There is no watch mode: run
`build/preview-site.sh --build-only` in a second terminal after an edit, and
reload the page.

## Publish

[.github/workflows/pages.yml](../.github/workflows/pages.yml) builds and deploys
the site when a change under `site/` reaches `main`. A pull request builds the
site and stops.

The repository's Pages source has to be **GitHub Actions** (Settings → Pages).
Until it is, the workflow fails at its first Pages step and publishes nothing.

## The front page's 3D mark

The mark in the front page's hero is the app's icon in pieces: the tile and its
three glass bars, turning slowly, each of which can be taken hold of and pulled
a little way. It is the model the app for iPhone, iPad and Mac shows on its
first run, with the same springs and the same glass.

- **The model** is `assets/models/SoloLogo.glb`, exported from
  `src/Solo.iOS/Models/SoloLogo.blend` by the script beside it:
  `blender -b SoloLogo.blend -P export_glb.py`. After changing the `.blend`,
  export it and commit both. The page finds each piece by its name in Blender.
- **The script** is `_hero/hero-logo.js`, written with
  [three.js](https://threejs.org), and with
  [cannon-es](https://github.com/pmndrs/cannon-es) for what makes the pieces
  solid to each other: a small physics library that is all JavaScript, chosen
  for its size over ones that are still being added to and are ten times it.
  `build/stage-site-assets.sh` bundles the three into `assets/js/hero-logo.js`,
  which is in `.gitignore`: about 185 KB compressed, loaded by the front page
  alone, with the libraries' licences at its end. The folder's name starts with an
  underscore so Jekyll leaves it, and its `node_modules`, out of the site.
  The libraries and esbuild are pinned in `_hero/package-lock.json`; to move to
  a newer one, change the version in `_hero/package.json` and run `npm install`
  there.
- **Where it stands.** Beside the words, as tall as the card, while the card is
  wide enough for both; in a narrower card it is above them. That goes by the
  card's own width (a container query in `docs.css`) and not the window's,
  since the card is narrower beside the sidebar than without it.
- **Without it** the page is whole. The hero holds the icon as a picture, and
  the scene takes its place once it has drawn; where there is no WebGL, or the
  script fails, the picture stays. Someone who has asked for reduced motion gets
  a mark that stands still until they move a piece.
- **To look at a change** to the script alone, after a build:
  `(cd site/_hero && node build.mjs) && cp site/assets/js/hero-logo.js site/_site/Solo/assets/js/`.

## Add or change a page

A page is a Markdown file with front matter:

```yaml
---
title: Boards
nav_title: Boards   # optional: a shorter name for the sidebar
lede: One sentence, shown under the title and used as the page description.
---
```

The layout prints the title, so the Markdown starts at the first `##` heading.
To put a new page in the sidebar, add its path to `_data/nav.yml`; the order
there is also the order of the Previous and Next links.

- **Links between pages** are relative paths to the `.md` file:
  `[Boards](boards.md#move-a-card)`. The build turns them into site addresses.
- **Callouts** are block quotes with a class on the next line: `{: .note}`,
  `{: .tip}`, `{: .warning}` or `{: .danger}`.
- **Screenshots** come from `docs/images/screenshots`, which the README uses
  too. `build/stage-site-assets.sh` copies them in at build time, so add a new
  one there and show it with `{% include shot.html src="name.png" alt="…" %}`.
- **Icons** are in `_includes/icon-sprite.html`. They are the paths the app
  draws, from `MudBlazor.Icons.Material.Filled`.

## Writing style

The pages follow Simplified Technical English (ASD-STE100) as a guide, so that
people who read English as a second language, and translation tools, get one
meaning from each sentence:

- One instruction in each sentence. Numbered lists for steps.
- 20 words or fewer in an instruction, 25 or fewer in a description.
- Active voice and simple tenses. No semicolons.
- One word for one thing: a *task* is always a task, the *sidebar* is always
  the sidebar. *Delete* means the thing is gone (a task, a project, data).
  *Remove* means it leaves a group and still exists (a member from a project,
  a task from a roadmap). *Clear* empties a field or a checkbox.
- UI text in bold, exactly as the app shows it.

## When the theme changes

`assets/css/docs.css` copies its colours, font and sizes from
`src/Solo.Api/Themes/ahlquist-interactive/theme.json`. Change them together.
