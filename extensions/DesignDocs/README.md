# Design Docs

Project vaults with collaborative Markdown, task links, images, sketches, and
Obsidian ZIP import/export. Complete offline downloads include notes, sketches, and images.
Requires Solo client contract 1.11 or later.
Offline startup also requires the updated Solo client. The foreground agent saves locally and synchronizes queued changes across pages.

Read the [user guide](../../site/guide/design-docs.md),
[sketch guide](../../site/guide/design-docs-sketches.md), and
[transfer guide](../../site/guide/design-docs-import-export.md) for features and limits.

## Build and install

From the repository root:

```sh
build/pack-contract.sh
build/pack-design-docs-extension.sh
dotnet test extensions/DesignDocs/Solo.DesignDocs.slnx
```

The build writes `artifacts/solo-design-docs-<version>.zip`.
Copy that package into Solo's extensions directory. Restart Solo.
For local development, `build/dev-design-docs.sh` creates a development package.
Set `Extensions__DevelopmentDirectories__0` to its printed path. Restart the host after changes.

The extension owns the `ext_design_docs` PostgreSQL schema.
Removing the package keeps its data. The host's purge action deletes that schema.
Core has no dependency on Design Docs.

See the [design record](../../docs/design-docs-plan.md#implementation-summary) for sync invariants and pending device checks.
See [preview builds](../../docs/prereleases.md) for isolated release channels.
