# Design Docs

Optional design-document vaults for Solo, requiring extension contract 1.11.
The extension owns `ext_design_docs`; uninstalling it removes its UI and endpoints
without changing core's data model.

Includes a live folder tree, collaborative Markdown, stable task/note links,
authenticated raster images, collaborative pixel sketches, and browser-based
Obsidian ZIP import/export. Super admins control vault lifecycle; project writers
edit content; Read members can view and export. Closed vaults are read-only.

Build from the repository root:

```sh
build/pack-contract.sh
build/pack-design-docs-extension.sh
dotnet test extensions/DesignDocs/Solo.DesignDocs.slnx
```

The package is `artifacts/solo-design-docs-1.0.15.zip`. Install it in the configured
extensions directory and restart Solo. For local development,
`build/dev-design-docs.sh` prints the development directory to configure; assembly
and static-asset changes require a host restart.

See [the design and validation record](../../docs/design-docs-plan.md) for the
protocol, access rules, implementation findings, and pending iPad/Pencil checks.
See [preview installation](../../docs/prereleases.md) for matching server and
desktop builds. The package includes dependency notices and the CC0 bitmap-font
license. Sketch text uses the active theme font.

Import preserves UTF-8 Markdown bytes, including BOM/line endings/front matter.
The preview explains skipped files and conflicts. Browser memory limits are
100 MB compressed import, 256 MB expanded import, 1 MB per note, and 512 MB
uncompressed export. Supported image formats are PNG, JPEG, WebP, and GIF;
oversized animated GIFs are skipped rather than flattened. Sketches export as
PNG with rewritten embeds in the exported copy only.

The feedback preview uses the active theme font for sketch text, floating MudBlazor
palettes, box/lasso multi-selection, live move previews, and viewer-only attachment
removal. The sketch Clear action uses the requested Deltarune explosion; asset
provenance is in `Solo.DesignDocs.Client/js/assets/NOTICE.txt`. Physical iPad/Pencil
and iPadOS backgrounding checks remain pending.

The insertion palette follows the current line at the left margin, with no line-number gutter. Sketch
frames are cached until shared strokes change; active strokes and selection moves
reuse those bitmaps. Paper is opaque, while its grid, ruled lines, or dots use 50%
opacity. Touch drawing starts off: one finger pans and Pencil draws. Toggle the
finger icon to draw with touch. The sketch dialog blocks header text selection
while preserving selection inside text inputs.

The notes sidebar adapts between 260 and 390 pixels, with compact nesting and
left-aligned titles. Long titles scroll on hover or keyboard focus, following
Roadmaps' ticker and reduced-motion behavior. Drag a title to move it; touch uses the same brief hold as the
board. Folders start collapsed, with per-vault cookies restoring expanded folders,
the current note, and the header's editor font size. Return continues Markdown
lists, Tab nests them, and Shift-Tab dedents. The insertion palette follows the
current line at the left text margin. Touch controls only appear on touch-capable
platforms. Safari Force Touch pressure is supported when exposed by the browser;
physical trackpad and iPad verification remain pending.

Sketch dialogs suspend Markdown input until they close. Sketch history loads as
one current state, and held selections use a floating preview beyond the paper
edge until release. The eraser uses Google's Material Symbols SVG; its attribution
and Apache 2.0 license are included in the package.


Preferences load before the vault becomes interactive, so a rerender cannot save
defaults over existing cookies. Margin width is configurable beside font scale.
The current note uses the theme primary color in the hierarchy. Compact note URLs
preserve every GUID bit; legacy links still work. H4/H5/H6 have heading styles.
Vault import skips videos with a warning and ignores common OS/vault metadata
silently. Unsupported video references stay in Markdown and never use the image
loader. The design record includes a future file/object-storage option for videos.

Note navigation retains the displayed editor until the incoming document finishes
syncing, with a darkened blur and circular progress indicator confined to the
Markdown pane. The hierarchy uses round caret hover targets and inset row
highlights; its action buttons align with expand/collapse-all. Attachment previews
animate into and out of the slideshow while its background fades. Reduced-motion
preferences disable these transitions. Native modal headers use the same 70-pixel
vertical axis as the desktop window controls.
