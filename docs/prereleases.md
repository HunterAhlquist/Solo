# Testing a preview build

`preview-v<version>-<label>.<number>` starts a complete test train from its tagged
commit, including an unmerged branch. For example,
`preview-v1.17.0-design-docs-codex.3` builds the server image, extension packages and
desktop installers from the same source revision.

The server workflow runs the core, extension and JavaScript suites before
publishing. It uploads the exact image tag
`ghcr.io/hunterahlquist/solo:1.17.0-design-docs-codex.3` and moves the floating `preview-codex`
tag. Agent labels select separate channels; unlabeled previews use `preview`. It never moves `latest` or a stable minor tag. The desktop workflow retains
its existing signing and notarization steps. Both attach assets to the same
GitHub **prerelease**, with `make_latest: false`; the releases remain visible to
whoever can see the repository. This is a release channel, not private hosting.

Stable `server-v…` and `desktop-v…` tags still require a commit on `main`. Their
change detection compares against earlier stable tags, so testing a preview
does not cause the eventual stable build to skip changed files. A prerelease
suffix on either existing series is treated as a preview too. The shared
`preview-v…` series is the convenient way to build both halves together.

## Start a train

After committing and pushing the branch, choose a fresh tag for each test build:

```sh
git tag preview-v1.17.0-design-docs-codex.3
git push origin preview-v1.17.0-design-docs-codex.3
```

Use a new number for the next build; do not move a tag already published. The
workflow files run from the tagged commit, so preview support need not land on
`main` first. No `sdk-v…` or `templates-v…` tag is involved, and the train does
not publish anything to nuget.org. Contract packages are built locally in CI.

Wait for both **CI** and **Desktop Release** to succeed. A failed job leaves its
logs and available workflow artifacts for diagnosis; rerun the failed job on
the same tag. Server and desktop assets may appear at different times while the
other workflow finishes. The desktop workflow can also be dispatched manually
on a branch for installer artifacts without creating a release.

## Install on your setup

In your test deployment's existing Compose or TrueNAS YAML, set the Solo service
image to the **exact** preview tag:

```yaml
services:
  solo:
    image: ghcr.io/hunterahlquist/solo:1.17.0-design-docs-codex.3
```

Keep the deployment's existing environment and volume configuration. Download
the extension ZIPs from that prerelease, replace their previous ZIPs in the
configured extensions directory, then recreate/restart Solo. Install the desktop
build from the same prerelease to get its matching client contract. The hosted
browser client comes with the server image.

Design Docs 1.0.8 includes vaults, collaborative notes, task links, images,
sketches, and Obsidian import/export. Physical iPad/Pencil and iPadOS
backgrounding checks are pending under the user-approved exception in
[the design plan](design-docs-plan.md#b0-findings-2026-10-22). This preview requires client contract 1.11; use its matching browser or desktop client.

To return to a stable build, restore the previous image and compatible extension
packages. Database migrations can make a simple image downgrade insufficient;
use a test database or a restorable database snapshot for a preview deployment.

References: [GitHub release management](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository),
[release action inputs](https://github.com/softprops/action-gh-release#inputs),
[Docker metadata tags](https://github.com/docker/metadata-action#tags-input).

## Parallel agent previews

Use a separate worktree and branch for each implementation. Agent-labeled tags
have the shape `preview-v1.17.0-design-docs-<agent>.<number>`, for example
`preview-v1.17.0-design-docs-codex.3`. The release classifier derives
`preview-<agent>` as the floating image channel and appends `-<agent>` to the
extension ZIP filenames. Agent builds never move the shared `preview` channel.
Desktop filenames and exact image tags include the agent label in their version.

The Codex feedback build uses Design Docs 1.0.8 and client contract 1.11. Its
release ZIP is `solo-design-docs-1.0.8-codex.zip`; the manifest and assembly keep
the numeric version so the host's upgrade/downgrade checks still apply. The
extension ID stays `design-docs`: do not install both implementations into one
extensions directory.

Each deployment also needs a separate app, cloned dataset, hostname, and port.
Codex's deployment is `solo-preview`, dataset `App Storage/Databases/Solo-Preview`,
HTTPS host `solo-preview.ahlquist.me`, and backend `10.0.0.4:30081`. Keep other
implementations out of those resources and production `solo`. No agent preview
should publish SDK/template tags or become the latest GitHub release.
