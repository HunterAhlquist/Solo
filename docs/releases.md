# Releases

One tag releases the whole train. Pushing `v<version>` runs
[ci.yml](../.github/workflows/ci.yml) for the server image and the extension
packages and [release-desktop.yml](../.github/workflows/release-desktop.yml) for
the installers, and both attach to **one** GitHub release for that tag. Exactly
one release exists per train, so marking it latest is unambiguous and nothing
gets buried behind a sibling release of the same commit.

```sh
git tag -a v1.18.0 -m "Solo 1.18.0"
git push origin v1.18.0
```

A stable tag has to be on `main`; both workflows refuse one that isn't.
`preview-v<version>` is the same train published as a prerelease and may sit on
an unmerged branch — see [prereleases.md](prereleases.md).

The SDK is not part of this. The contract packages and the `dotnet new`
templates go to nuget.org from their own `sdk-v*` and `templates-v*` tags
([release-sdk.yml](../.github/workflows/release-sdk.yml)), because the contract
moves independently of the app and a NuGet push is permanent.

## Only what changed gets built

Each component is diffed against the previous `v*` tag the commit descends
from, over the inputs that can actually change it:

| Component | Rebuilt when these changed |
|---|---|
| Server image | `Dockerfile`, `.dockerignore`, and the project-reference closure `Solo.Api` publishes |
| Desktop installers | `src/Solo.Desktop`, `src/Solo.Client`, `src/Solo.Shared`, `Icons/`, the desktop workflow |
| Each extension package | that extension's two projects, its pack script, `extensions/nuget.config`, and the contract (`src/Solo.Extensibility`, `src/Solo.Shared`) |

A component whose inputs are untouched is not built and not attached. A
test-only or docs-only commit therefore releases nothing but notes; an extension
that didn't change does not get a second identical zip on a new release.

The first `v*` tag has no earlier one to measure against, so it builds and
attaches everything. That is also what establishes the complete manifest every
later release carries forward from.

## `release-manifest.json`

Because a release holds only the parts that changed, its assets are an
incomplete picture of the product: release N can hold new extension packages
while the current installers still live in release N-2. Every release carries a
`release-manifest.json` that closes that gap.

**The newest release's manifest always describes every component at its current
version, and every entry names the release whose assets it points at.** An entry
for something this train didn't rebuild is carried forward untouched, download
URL included, so it keeps resolving.

```json
{
  "schema": 1,
  "release": { "tag": "v1.18.0", "version": "1.18.0", "prerelease": false, "published": "…" },
  "server":  { "version": "1.17.0", "release": "v1.17.0", "image": "ghcr.io/hunterahlquist/solo:1.17.0", "contract": "1.11.1" },
  "desktop": { "version": "1.18.0", "release": "v1.18.0", "contract": "1.11.1",
               "channels": { "mac": "…/latest-mac.yml", "win": "…/latest.yml", "linux": "…/latest-linux.yml" },
               "installers": [ { "platform": "mac", "asset": "…", "url": "…", "size": 0, "sha512": "…", "primary": true } ] },
  "extensions": [
    { "id": "design-docs", "version": "1.3.0", "release": "v1.18.0", "asset": "solo-design-docs-1.3.0.zip",
      "url": "…", "size": 0, "sha512": "…", "soloApiVersion": ">=1.10 <2.0", "soloClientVersion": ">=1.11 <2.0" }
  ]
}
```

It is one file because the three updaters this exists for all read the same
thing, at `/releases/latest/download/release-manifest.json`:

- **Server update notification.** Compare `server.version` with the running
  version and point an operator at `server.image`.
- **Extension auto-update.** For each installed extension, compare
  `extensions[].version` with what's on disk and fetch `extensions[].url`.
  `soloApiVersion` is checked against the server's `server.contract`
  (`ContractVersion.Current`) and `soloClientVersion` against the running
  client's, which in the desktop shell is `desktop.contract` — so an updater can
  refuse a package the host could not load *before* downloading it.
- **Desktop updates.** `desktop.channels` are the electron-updater channel
  files, described below.

A third-party extension publishes the same shape on its own GitHub release, with
only an `extensions` entry, so the host reads one format whoever built the
package.

`schema` is the version of this contract. Add fields freely; bump it only when
an existing field changes meaning.

Both workflows merge into the one manifest on the release, serialised by a
concurrency group they share. Whichever gets there first seeds it from the
previous release, so neither has to run for the result to be complete.
[build/release-manifest.mjs](../build/release-manifest.mjs) does the merge; it
reads each extension's entry out of the `extension.json` inside the package
rather than having the workflow restate it.

## The desktop update feed

The installers carry a `generic` electron-updater feed pointing at
`https://github.com/hunterahlquist/Solo/releases/latest/download/`, baked in at
packaging time as `app-update.yml`.

Two constraints shaped that, and both are easy to undo by accident:

- **It cannot be the `github` provider.** That provider rebuilds every download
  URL from the release tag it found the feed on, so a feed served from release N
  can only reference assets in release N. This train deliberately leaves
  installers in the release that built them, so the URLs have to be absolute —
  which only a generic feed's are.
- **The newest release must always carry a channel file.** electron-updater
  refuses to look past the newest release; a 404 there is a broken update check,
  not a "no update". So when a train doesn't rebuild the installers, the release
  job copies the previous release's `latest*.yml` onto the new release verbatim.
  They're a few hundred bytes each and their URLs already point at the release
  that holds the matching installers, so nothing is uploaded twice.

[build/updater-feed.mjs](../build/updater-feed.mjs) writes those files from the
uploaded bytes rather than letting electron-builder generate them — partly for
the absolute URLs, and partly because `scripts/after-all-artifact-build.mjs`
re-encodes the `.dmg` as ULMO *after* electron-builder has hashed it, so any
digest taken during packaging is stale.

macOS updates install from a **zip**, not the `.dmg` — Squirrel.Mac cannot use a
disk image. Both are built and attached, and the feed names the zip. Dropping
the `zip` target from `package.json` would leave macOS unable to update while
everything still looked fine.

Preview builds get no feed: they're prereleases, which the `releases/latest`
redirect skips, so the feed they'd read describes the stable app. Previews are
installed by hand.

## Installer names

Every installer is named explicitly in
[electron-builder.config.cjs](../src/Solo.Desktop/electron-builder.config.cjs),
free of spaces and brackets, because **GitHub rewrites a space in a release
asset's name to a dot**: `Solo (Preview) Setup 1.18.0.exe` arrives as
`Solo.Preview..Setup.1.18.0.exe`, and every URL the feed wrote for it points at
nothing. The default names derive from `productName`, which is `Solo (Preview)`
on the preview channel. `scripts/check-app-identity.mjs` enforces this, and
`build/updater-feed.mjs` refuses a name with a space in it.

## What a release holds

| Asset | From |
|---|---|
| `release-manifest.json` | always |
| `latest-mac.yml`, `latest.yml`, `latest-linux.yml` | always — fresh, or carried forward |
| `Solo-<version>-arm64.dmg` / `.zip`, `Solo-<version>-setup.exe`, `Solo-<version>.AppImage` | only when the desktop changed |
| `solo-<extension>-<version>.zip` | only for extensions that changed |

The server image is not a release asset; it goes to GHCR as
`ghcr.io/hunterahlquist/solo` tagged `<version>`, `<major>.<minor>`, `latest`
and `sha-<sha>`.

## Failure modes worth knowing

- A changed server that fails to push to GHCR blocks its own release.
- A desktop build that fails still gets the previous feed carried forward, so
  installed apps keep resolving to the last good installers rather than seeing a
  404. The run itself reports the failure.
- A partially failed build matrix attaches no installers at all and carries the
  whole feed forward: a feed naming a platform whose installer is missing is
  worse than an unchanged one. Its hand-off artifacts are kept so a re-run can
  recover the installers that did build.
- **Nothing is published unless every step that produces it succeeded.** The
  steps from resolving the baseline through the release upload carry no `if:` at
  all, so GitHub's default gating applies and any failure among them stops the
  publish. That is deliberate and
  [linted](../build/test-release-gating.sh): three review rounds in a row found
  a release published after a producing step had failed, every time because a
  hand-written condition had to name every producing step before it and missed
  one — and `!cancelled()` made it worse by dropping the implicit success check,
  so a guard could `exit 1` while the upload ran anyway.
- **Exactly one failure is tolerated: building the updater feed.** It is
  `continue-on-error`, so the steps that keep the release usable still run — the
  previous feed is carried forward and the manifest is assembled to match it —
  and the *last* step in the job re-asserts the failure. The release stays
  consistent; the run still goes red.
- **The newest release always carries a channel file.** The carry-forward is
  keyed on whether a feed is actually present, not on whether the build job ran,
  so it covers the feed failing to generate as well as never being built. If
  there is nothing to carry forward and a desktop app that reads the feed has
  already shipped, the job refuses rather than publishing. Before the first
  desktop release there is nothing out there to break, so that case only warns.
- **Installers no feed describes are discarded rather than attached.** If the
  feed write failed, the installers it would have described are undescribed by
  the manifest too, and a release must not offer a build nothing vouches for.
- **An unreadable release is fatal, never guessed at.**
  [build/release-baseline.sh](../build/release-baseline.sh) establishes a
  release's absence from a 404 and nothing else, because both wrong guesses
  publish a broken release: treating *this* release's manifest as absent
  overwrites the other workflow's fresh entries with older ones, and treating
  the *previous* one as absent publishes a manifest describing only what this
  train rebuilt — silently dropping the desktop app and every unchanged
  extension from the file every updater reads. The publishing steps therefore
  also require `steps.baseline.outcome == 'success'`.

The invariant is inductive: it holds for every release because it held for the
one before. The first `v*` release is what establishes it, which is the other
reason that release builds everything.
