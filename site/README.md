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
Docker and python3. There is no watch mode: run
`build/preview-site.sh --build-only` in a second terminal after an edit, and
reload the page.

## Publish

[.github/workflows/pages.yml](../.github/workflows/pages.yml) builds and deploys
the site when a change under `site/` reaches `main`. A pull request builds the
site and stops.

The repository's Pages source has to be **GitHub Actions** (Settings → Pages).
Until it is, the workflow fails at its first Pages step and publishes nothing.

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
