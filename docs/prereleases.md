# Preview builds

A `preview-v<version>` tag builds the server, extension packages, and desktop installers from one commit.
It is the stable `v<version>` train published as a prerelease — see [releases.md](releases.md).
Preview commits can remain on an unmerged branch.
A stable `v…` tag requires a commit on `main`.

## Publish a preview

Use a new tag for each build:

```sh
git tag -a preview-v1.17.0-design-docs-codex.17 -m "Codex preview 17"
git push origin preview-v1.17.0-design-docs-codex.17
```

Never move an existing preview tag. Wait for **CI** and **Desktop Release** to succeed.
The server workflow tests core and extensions before publication.
The desktop workflow keeps its signing and notarization steps.
Both workflows attach assets to the same GitHub prerelease.
A prerelease is visible to anyone who can access the repository.

The example produces the exact image tag `ghcr.io/hunterahlquist/solo:1.17.0-design-docs-codex.17`.
It also updates `preview-codex`, the floating Codex preview tag.
It never changes `latest` or stable minor tags.
Preview publication does not publish NuGet packages or create SDK/template releases.

## Install a preview

1. Create a separate app and database dataset for testing.
2. Snapshot the dataset before an update.
3. Select the exact preview image tag in that app's Compose configuration.
4. Copy matching extension ZIPs into its extensions directory.
5. Remove previous ZIPs for those extension IDs from the active directory.
6. Restart the preview app.

Keep previous packages as backups outside the active extensions directory.
Database migrations can prevent a simple image downgrade. Restore a compatible snapshot when required.
Use a matching desktop client when the preview changes the client contract.
The browser client comes with the server image.

See the [Design Docs guide](../site/guide/design-docs.md) for extension features.
The [design record](design-docs-plan.md#validation) lists pending device checks.

## Separate parallel previews

Tags use `preview-v<version>-design-docs-<agent>.<number>` for parallel implementations.
The agent label selects `preview-<agent>` and adds `-<agent>` to extension ZIP filenames.
The manifest keeps its numeric version. The extension ID remains `design-docs`.
Install only one implementation per extensions directory.

Each implementation needs its own app, dataset, hostname, and port.
The existing Codex environment uses these resources:

| Resource | Value |
|---|---|
| App | `solo-preview` |
| Dataset | `App Storage/Databases/Solo-Preview` |
| HTTPS hostname | `solo-preview.ahlquist.me` |
| Backend | `10.0.0.4:30081` |

Keep other implementations outside these resources and production `solo`.

References: [GitHub releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository),
[release action inputs](https://github.com/softprops/action-gh-release#inputs),
[Docker image tags](https://github.com/docker/metadata-action#tags-input).
