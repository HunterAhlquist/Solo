# Updates

[releases.md](releases.md) is about producing a release. This is about consuming
one: how an instance, a desktop app and an installed extension each find out
they are behind, and what happens when someone acts on it.

All three read the same file — the `release-manifest.json` on the newest
release — for the reason given there: a release holds only the components that
changed, so its assets are an incomplete picture and the manifest is what closes
the gap.

| What | How it checks | What acting on it does |
|---|---|---|
| Server | The instance reads the manifest and compares `server.version` | Sends the operator to the GitHub release. Pulling the image stays manual. |
| Desktop app | electron-updater, against the channel files in the same release | Downloads on confirmation, installs on quit |
| Extensions | The instance compares each installed package against the manifest | Downloads, verifies, replaces the package and restarts the instance |

## The server indicator

A super admin gets an icon in the app bar when a newer release exists, linking
to it on GitHub. Admin settings shows the same check with its detail, including
the image reference to pull.

Only a super admin, and only when there is something to act on. Updating the
server means pulling a new image; for anyone else the badge is something they
can neither action nor dismiss. The app bar stays silent when the check fails or
is disabled — there is no icon that honestly means "we don't know" — and Admin
settings says which it was.

**The check is server-side, and has to be.** The client's CSP is
`connect-src 'self' https://accounts.google.com` (`SecurityHeaders.cs`), so the
browser cannot fetch anything from GitHub. Checking on the server also means one
request per instance rather than one per open tab, and the answer is cached for
six hours.

Nothing about the instance is sent: it is an unauthenticated GET of a public
file. `Updates__Enabled=false` switches it off; `Updates__ManifestUrl` points it
at a fork or an internal mirror.

## The desktop app

The installers carry a `generic` electron-updater feed baked in at packaging
time, pointing at `releases/latest/download/`. See
[releases.md](releases.md#the-desktop-update-feed) for why it is that provider
and why the newest release always carries a channel file.

`autoDownload` is off. An installer is well over a hundred megabytes, and
spending that before the user has agreed is not a reasonable default. The flow
is: check on startup (eight seconds in, so the window paints first) and from
**Check for Updates…** in the menu → a dialog naming the version and the
download size → download on confirmation, with progress on the dock or taskbar
icon → install on quit, or restart now.

`autoInstallOnAppQuit` stays on, so an update that was downloaded applies at the
next quit whether or not the user took the "Restart Now" offer.

Four situations skip the check, because electron-updater cannot work in them:

- **a development run**, which has no `app-update.yml`;
- **a preview build**, whose publish config is `null` deliberately. The feed
  points at the `releases/latest` redirect, which skips prereleases — so a
  preview that checked would be told the stable line supersedes it and would
  replace itself with a *different app*: different appId, different settings,
  different user data. `scripts/check-updater.mjs` pins this.
- **Linux outside an AppImage**, the only Linux target electron-updater can
  replace in place.
- **a build with no `app-update.yml`.** electron-builder writes that file per
  *target*, so `--publish never` is fine but a `--dir` pack produces a perfectly
  good `.app` with no update config in it — and copying that out of `dist/`
  instead of installing from the `.dmg` is the normal way to try a local build.
  Without the check, electron-updater reads the missing file and the user gets a
  raw `ENOENT` and a path. Verified both ways: a `--dir` pack writes no such
  file, and a real target build writes `provider: generic` with the feed URL.

A check the user asked for reports those out loud. The one on startup is silent,
and so is a failed one — the next launch tries again, and a dialog about a
network that was briefly down is worse than nothing.

## Extensions

Admin settings shows an update beside each installed extension, and an Update
button that downloads it, replaces the package and restarts the instance.

**The restart is not incidental.** An extension registers its controllers, hooks
and DbContext before the host is built, so there is no rescan and a new package
can only take effect at a boot — which is why installing one has always needed a
restart. The instance comes back because something restarts it
(`restart: unless-stopped` in the compose file). On a host with no restart policy
it stays down until started by hand, and the confirmation says so.

### Where the update comes from

Two feeds can describe an extension:

- **Solo's own release manifest**, for the extensions that ship with a release.
- **the extension's own**, named as `updates` in its `extension.json` — an
  absolute `https` URL of a `release-manifest.json` with a single `extensions`
  entry. This is what gives a package from outside this repository the same
  update path as a first-party one. Added in contract 1.12.0.

Solo's manifest wins for a package it describes: those two versions were
released together, so that entry is the one tested against this server. A
first-party extension therefore costs no extra request.

### What it refuses, and why

Installing an extension is deploying code that runs in-process with the database
connection. The feed says where the bytes come from; nothing takes its word for
what they are. In order, before anything is written:

- the package must have been loaded from a `.zip` in `Extensions__Directory` —
  a development directory has no zip to replace, and overwriting a developer's
  working tree is not this feature's business;
- that directory must be **writable**, probed by writing rather than inferred
  from permission bits, because the failure being guarded is a read-only mount.
  The message names UID 1654, since the usual cause is a host path owned by
  someone else;
- the URL must be `https`, and it is fetched through the same handler as the
  avatar proxy, which **only connects to public addresses**. An extension author
  chooses that URL, so it is an SSRF vector into the container network
  otherwise; the guard is on the socket, so it survives redirects and a DNS name
  pointing at a private address (`PublicOnlyHttpHandler`);
- the download must match the `sha512` **and** the size in the feed;
- the zip must then actually **load as a package** whose id is the one being
  updated and whose version is the one the feed offered. A digest proves the
  bytes are the ones named; it proves nothing about what they contain;
- **both** declared contract ranges must admit this instance. Checked before
  the update is *offered*, not after it is installed: an out-of-range package is
  refused at the next boot and reported as a load failure, which to whoever
  clicked Update is indistinguishable from the update having broken the
  extension. Such an update is shown, with the reason, and no button.

  `soloClientVersion` matters as much as `soloApiVersion` and is the easier one
  to forget, because the two fail differently. A server half out of range never
  loads, loudly. A **client** half out of range loads on the server and is then
  skipped in the browser — so the extension keeps working except that its pages
  are gone, reported on the same page as "Client pages unavailable". The client
  range is compared against this build's contract, which is what the hosted
  client ships with; a desktop shell older than the instance bundles its own
  client and may still skip the pages, which the shell already warns about.

Only then are two renames done in one directory: the old package to
`solo-<id>.rollback.zip.bak`, the verified download to its installed name. The
backup is inert — the loader only scans `*.zip` — so a bad update is recoverable
with shell access alone by renaming it back.

**One rollback slot per extension, overwritten each time.** It is named for the
extension rather than for the package it holds, which matters: naming it after
the package meant every update minted a *new* `.bak` and replaced none, leaving
a multi-megabyte copy of every version ever installed in the volume and nothing
to say which one was the rollback. A fixed name is the entire mechanism —
overwriting bounds growth with nothing having to go looking for files to delete.

An update therefore touches exactly two files and **removes nothing else**, by
pattern or otherwise. Anything else you keep in that directory is yours: a
sweep of `solo-<id>-*.zip.bak` would match `solo-<id>-pro-*.zip.bak`, since
extension ids may contain hyphens, and delete a different extension's only
recovery package — and that is also exactly what an operator keeping a copy of
a package would name it.

The old zip is **moved** rather than left in place because two zips declaring one
id make the loader reject the second as a duplicate, which would present as the
extension vanishing.

The asset name from the feed is used only when it is a plain `.zip` file name;
anything else and the installer builds the name itself. A feed is remote input
and must not be able to choose a path.

## Publishing updates for a third-party extension

Publish a GitHub release carrying a `release-manifest.json` with one
`extensions` entry, and point the package's `updates` at it:

```json
{
  "schema": 1,
  "extensions": [
    {
      "id": "my-extension",
      "version": "1.4.0",
      "asset": "solo-my-extension-1.4.0.zip",
      "url": "https://github.com/me/my-extension/releases/download/v1.4.0/solo-my-extension-1.4.0.zip",
      "size": 148231,
      "sha512": "<base64 sha512 of the asset>",
      "soloApiVersion": ">=1.12 <2.0",
      "soloClientVersion": ">=1.12 <2.0"
    }
  ]
}
```

Serve it from a stable URL — `releases/latest/download/release-manifest.json`
does this on GitHub, resolving to the newest non-prerelease release. Get
`soloApiVersion` right: it is what stops an instance being offered a package it
could not load.

## Settings

| Setting | Default | Notes |
|---|---|---|
| `Updates__Enabled` | `true` | `false` disables every check; the UI says so rather than implying the instance is current |
| `Updates__ManifestUrl` | this repository's latest release | A fork or internal mirror |
| `Updates__ReleasesUrl` | this repository's releases page | Where the indicator links |
| `Updates__CacheFor` | `06:00:00` | How long a fetched manifest is reused |
| `Updates__Timeout` | `00:00:10` | Per feed read; short, because the answer is advisory |
| `Updates__DownloadTimeout` | `00:05:00` | Per package download |
| `Updates__MaxPackageBytes` | 64 MiB | Guard against a feed naming an implausible asset |

A failed check is cached for ten minutes rather than six hours, so a feed that
is down is not re-requested on every page load but fixing it does not mean
waiting out the success cache.
