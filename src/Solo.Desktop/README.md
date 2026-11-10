# Solo Desktop

An Electron shell around the Solo web client each instance serves. The shell
gives an instance a local origin (`http://localhost:41321`) and reverse-proxies
every path on it to that instance — the client's own files, REST, images and
SignalR WebSockets — so the window runs the very client the instance hosts,
same-origin, with no CORS anywhere and no changes to the API. It bundles no
client of its own, so it never falls behind the server. It can hold several
instances, each in a window and origin of its own.

Why a local origin rather than the instance's own address: each instance keeps
its own storage and session, and `http://localhost` is a secure context even
for an instance on plain http, which `crypto.subtle`, `crypto.randomUUID` and
the client's offline mode (a service worker) all require. The instance's own
headers come through untouched, its Content Security Policy among them.

When the instance can't be reached and the client's offline mode hasn't cached
it, a window shows the shell's own "Can't reach the instance" page with **Try
again** and **Instances**. A fetch from a client that is running gets JSON,
which the client already handles.

## Running

```sh
cd src/Solo.Desktop
npm install
npm run build:native   # macOS only: the sign-in window add-on (see Sign-in)
npm start
```

On first launch a settings window asks for an instance address
(e.g. `https://solo.example.com`). Settings live in `settings.json` under
Electron's `userData` directory.

## Preview builds

Prerelease versions install as **Solo (Preview)** with a separate app identifier
and the icon from `Icons/Solo Preview.icon`. Settings, Chromium cache/storage,
logs, and crash dumps live under **Solo (Preview)** inside the platform's app-data
directory (`~/Library/Application Support/Solo (Preview)` on macOS). Stable
profiles are not copied: add and sign in to the preview's instances once.
Both channels can run together; occupied local ports are skipped when adding an
instance. Manual release-workflow builds are previews too.

On macOS, connecting to a LAN instance triggers the system's Local Network
permission request. Allow it and retry if the first connection failed while the
prompt was open. Each packaged main/helper launcher receives a unique Mach-O
build UUID before signing, so macOS can track its permission independently from
other Electron apps ([Apple TN3178](https://developer.apple.com/documentation/technotes/tn3178-checking-for-and-resolving-build-uuid-problems),
[TN3179](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)).
The Electron framework remains untouched; only the small launchers lose their
match to Electron's stock debug-symbol UUID.

Every instance opens a separate origin and Blazor runtime, with its own
extension catalog and browser storage. Releases before 1.21 bundled their own
client, which could fall behind the instance; from 1.21 a server update reaches
the desktop app's client at once.

## Instances

*Solo → Settings…* (`Cmd/Ctrl+,`; under *File* on Windows and Linux) lists the
instances the app knows. **Add** takes one through three steps:

1. **Test.** An anonymous fetch of `/api/config/auth` confirms the address is
   a Solo instance. One that fails is not added.
2. **Sign in.** The instance is saved and the browser sign-in below starts.
3. **Open.** When the sign-in lands, the instance opens in a new window,
   already signed in, and the settings window closes.

An instance that doesn't sign in with Google, or hasn't finished its setup,
has no browser sign-in to run, so it skips step 2 and you sign in (or set it
up) in its window.

An instance is listed under the display name its own operator gave it (Admin
settings, served with `/api/config/auth`), and under its address until it has
one. The shell asks again on every page load, and the client reloads when the
name is changed, so a rename shows up in the menu at once. Two instances going
by the same name get their addresses added.

*Solo → Open Instance* lists them all. An instance has at most one window, so
one that is already open is greyed out there — the *Window* menu brings it
forward, and its title says which instance it is once there are two.
Choosing one that isn't open asks whether it gets a **new window** or takes
over the **current window**, the instance window used last. The app reopens
the instance used last when it starts, and quits when its last window closes.

Each instance is served on a local port of its own, counting up from `41321`
(`41322` for an unpackaged run, so it can start beside the installed app), and
the port is what keeps them apart: the client stores its session in
`localStorage`, which the browser scopes to the origin, so a session minted by
one instance is never sent to another. The port is saved with the instance
because a different port is a different origin — the sign-in stored under the
old one would be gone. Removing an instance clears what was stored under its
origins, since its ports are reused. It does not sign the session out on the
server; do that from the app first, or from *Signed-in devices* on the web.

When another program holds an instance's port, the instance moves to another
port of its own instead of failing to start (`main/ports.js`): one it already
owns, or else the next one nobody owns, in the desktop's range of 100 ports
from `41321` (the iPhone and iPad app uses `43321` up, since a simulator or the
iPad app on a Mac shares this machine's loopback). This is silent: the window
just opens there, on the instance's sign-in page, and the move is logged. It
stays on that port from then on, because moving back when the old port came
free would strand what was stored on the new one. What was stored under the old
port, unsynced offline edits included, is kept there: setting `port` back to it
reaches it again. No two instances ever own the same port, and the port is
chosen once per instance even when two windows open it at once
(`main/local-servers.js`); `scripts/check-instance-ports.mjs` checks these
rules. Only when none of the 100 ports can be had does the app say it couldn't
open the instance.

`settings.json` holds `instances` (`id`, `name`, `url`, `port`, `ports`):
`port` is the port the instance opens on and `ports` every port it owns.
`port` can be edited there while the app is closed, to any of its `ports` or a
port no other instance owns, and `name` is only the last name the
instance gave. A file from before the
shell knew more than one instance, with a lone `instanceUrl`, is read as that
one instance on its old port, so it stays signed in.

During development, point an instance at a local server
(`dotnet run --project src/Solo.Api`) to try a client change: the window loads
whatever that server serves.

### Glass backdrop (macOS)

The main window uses a translucent backdrop by default while leaving the
settings window and non-macOS builds unchanged. On macOS 26 and later Solo
uses the native Liquid Glass module. If that module is unavailable, unsupported,
or cannot attach to the window, Solo falls back to Electron's AppKit vibrancy:

```sh
npm start
```

To disable both treatments for troubleshooting, launch with
`SOLO_DISABLE_LIQUID_GLASS=1 npm start`.

The backdrop follows Solo's Light, Dark, or Match system preference rather than
independently inheriting macOS. Changes apply to the native material while the
app is running. The Blazor loading view uses the same theme-tinted material from
the first paint instead of covering the native backdrop with an opaque splash.

The Settings window uses the same native treatment. Its macOS native
window effects checkbox disables both Liquid Glass and the vibrancy fallback
after the next restart; the choice is stored with the other shell settings.

Under it, **Background opacity** sets how much of the theme's own page
background is painted over the effect: 0% (the default) is bare glass, 100% is
the theme's ordinary opaque background. The slider previews on every instance
window as it moves, and is saved when it is let go, without a restart. It is
`backgroundAlpha` (0–1) in `settings.json`, and reaches the client as the
`--solo-glass-background-alpha` custom property the preload sets on `<html>`.

## Window chrome

The window has no OS title bar. The platform draws its own controls over the
client's app bar instead — inset traffic lights on macOS, the Window Controls
Overlay on Windows and Linux (where the menu bar auto-hides behind `Alt`) — and
the app bar is the region you drag the window by.

That takes agreement from both sides: the preload puts `solo-desktop`,
`solo-desktop--mac` / `solo-desktop--overlay`, `solo-desktop--fullscreen`, and
`solo-desktop--glass` on `<html>`, and the client's `app.css` keys its drag
regions, transparency, and room for the buttons off them (see the *Desktop
shell chrome* block there). The classes exist only inside the shell, so the
hosted web build keeps an ordinary app bar.

## Context menus

Electron has no right-click menu of its own, so `main/context-menu.js` supplies
two kinds:

- **The editing menu**: spelling suggestions, then Undo, Cut, Copy, Paste and
  Select All as the click target allows. It's built from Chromium's
  `context-menu` event, which fires only when the page leaves the right-click
  alone.
- **The page's own menus**: when the client handles a right-click itself (a
  roadmap's bar or day menu, say), it prevents the browser's menu and asks for
  one through `soloDesktop.showContextMenu(items)`. Main draws a native menu
  from the labels and ids and answers with the one chosen, or null. Items are
  checked rather than trusted: extension code runs in the page, so it gets plain
  labels and nothing that names a role, an accelerator or a shell action. The
  client asks for this through `ISoloContextMenu` and draws its own menu when
  the bridge is missing, which is how the hosted web build works.

## Sign-in

Google refuses to run its sign-in pages inside Electron ("this browser or app
may not be secure"), so the shell signs in outside the app on the
**instance's own origin** — on macOS in a **sign-in window** over the app,
elsewhere in the **default browser** — then returns through a PKCE-bound
loopback callback:

1. The in-app **Authorize in browser** button opens an ephemeral `127.0.0.1`
   listener, generates a PKCE verifier, and starts a flow (`POST
   api/auth/desktop/start`) with that port and the verifier's challenge.
2. The shell opens `https://<instance>/desktop-auth?state=…`. The page wears
   the instance's theme, signs in with Google, and posts the credential back
   to the instance. The instance verifies it and redirects a short-lived,
   one-time authorization code to the loopback listener.
3. The shell redeems that code with its verifier (`POST
   api/auth/desktop/redeem`) and receives a **long-lived Solo session token**
   (sliding expiry, default 30 days via `Auth:SessionDays`), exactly once. A
   remote client never receives the loopback code, and an intercepted code is
   useless without the verifier.

Signing out revokes the session server-side. Both the desktop app and instance
must support the secure loopback flow; incompatible versions fail closed, and
the current desktop app identifies an old server response and asks for an
update. (The
client detects the shell via `window.soloDesktop`, exposed by the preload,
and swaps the GIS widget for the plain button — Google's widget never loads
inside the app.)

### The macOS sign-in window

On macOS, `main/auth-sheet.js` shows the page in Apple's web authentication
window (`ASWebAuthenticationSession`) instead of a browser tab. The native
half is `native/auth-session`, a Node-API add-on built by `npm run
build:native` (macOS only; one build loads in any Electron version). Google
allows its sign-in there, unlike in an Electron window, and the window shares
the cookies of Safari — or of a default browser that supports it — so an
existing Google sign-in carries over.

- The window is the browser's, so opening it brings the browser and its other
  windows forward. The app comes back to the front once the session is
  delivered.
- On success the loopback listener answers with a redirect to
  `com.solo.desktop://desktop-auth/done`. The window closes itself on that
  scheme; nothing else registers or handles it.
- **Cancel** in the window ends the flow with "Sign-in was cancelled." in the
  app. A failure closes the window and shows the error in the app.
- Where the add-on is missing — Windows, Linux, or an unbuilt checkout — sign-in
  falls back to the default browser.

### Popup or redirect

By default the page shows Google's button, whose sign-in opens in a popup: a
second window, after a click. With `Auth__DesktopRedirect=true` on the
instance, the page goes straight to Google's OpenID Connect sign-in in the
same window instead (`response_type=id_token`, `response_mode=form_post`),
and Google posts the token and state back to `POST /desktop-auth`. That
address must be an authorized redirect URI of the Google client, which is why
it is opt-in. Either way `complete` requires the ID token's `nonce` to be the
flow's state.

**Other OIDC providers** (Microsoft Entra ID, generic) sign in through the
same page, with the authorization code flow and PKCE: the page goes straight
to the provider, comes back to `GET /desktop-auth?code=…&state=…`, and trades
the code for an ID token at the provider's token endpoint, with no client
secret. Register `https://<instance>/desktop-auth` as a redirect URI of the
client: for Entra ID, under **Single-page application**, beside the web app's
own. The iPad app signs in through this page too, so one registration covers
both apps. The window itself never runs the provider's pages: this shell's CSP
allows no provider but Google.

## MCP

The shell no longer serves an MCP endpoint. It moved to the instance, where it
belongs: [extensions/Mcp](../../extensions/Mcp) is an extension the server loads, so MCP
works from any machine and any client rather than only while this app is
running, and access is granted per user by a token the owner can rotate and an
admin can audit and revoke.

Install the package on the instance and generate a token from **MCP access** in
the profile menu — the page produces the config snippet each client wants. See
the [MCP server section](../../README.md#mcp-server-bundled-extension) of the
root README.

## Packaging

```sh
npm run dist   # electron-builder → dist/ (arm64 dmg, nsis, AppImage)
```

macOS ships for Apple silicon only. A universal build carries a second copy of
Chromium, which nearly doubled the download (178 MB against 96 MB) only to
serve Intel Macs. `electron-liquid-glass` installs both darwin prebuilds,
so `files` drops the x64 one rather than ship and sign a binary that can never
load.

`mac.signIgnore` is what keeps signing to seconds. `@electron/osx-sign` runs
`codesign --timestamp` once per file that merely *looks* binary, one at a time,
and it follows the framework symlinks, so it reaches every file inside a
framework up to three times. Chromium's ~220 `locale.pak` files alone came to
660 of 730 calls and about 90 s. The two patterns skip non-code resources
(`.pak`, `.dat`, `.bin`, `.nib`, `.asar`, `.car`, `.icns`) and the symlinked
paths into each framework. That leaves about 26 calls, one per real Mach-O and
bundle, which is exactly what notarization requires. The resources stay sealed
by their bundle's signature, the same way Xcode signs an app. If a dependency
ever ships a Mach-O under one of those extensions, it goes unsigned and
notarization rejects the build. It fails loudly, not silently.

Two things keep the `.dmg` small:

- It carries no client: the shell proxies the one each instance serves.
- `scripts/after-all-artifact-build.mjs` re-encodes the image as ULMO (lzma),
  which electron-builder's `dmg.format` doesn't offer. It is about a fifth
  smaller than the default zlib. `dmg.writeUpdateInfo` is off because there is
  no auto-updater to read a blockmap, and converting would invalidate it
  anyway.

The installers carry the brand rather than electron-builder's defaults. The
disk image opens on a two-column Solo board (drag the app from Unstarted to
Finished, i.e. into Applications), and Windows uses the guided NSIS installer
(`nsis.oneClick: false`) because the one-click one has nowhere to show any art.
Its sidebar and header are the dark and light schemes. The artwork is HTML in
`scripts/installer-art/`, rendered by Electron into `build/` with:

```sh
npm run build:installer-art   # background.png/@2x, installerSidebar.bmp, installerHeader.bmp
```

The outputs are committed, so CI never renders them. The disk image's icon
positions in `dmg.contents` must line up with the layout in `dmg.html`. Run the
command after changing either, or after `build:icon`, since it draws
`build/icon.png`. Electron can't start inside a sandboxed shell, so run it from
an ordinary terminal.

electron-builder's `latest` dist-tag on npm has lagged the newest 26.x before
(it sat at 26.15.3 while `v26` was at 26.17.0). When it does, `npm outdated`
reports the pinned version as *ahead* of latest and a bare
`npm install electron-builder@latest` quietly downgrades it, so check
`npm view electron-builder dist-tags` before bumping. The floor here is
`^26.18.1` so the range itself refuses an older build; `npm ci` follows the
lockfile either way.

The minimatch overrides move `app-builder-lib`'s own 10.x and
`@electron/universal`'s 9.x to 11, the first line patched for
GHSA-g3hv-75wg-m249. Both callers use the named `minimatch` export, which 11
kept. The 3.x and 5.x copies deeper in the tree (`@electron/asar`,
`dir-compare`, `glob` 7, `filelist`) call the package as a function, which 11
no longer exports, so the selectors keep the overrides off them. Those lines
predate the preprocessing the advisory describes (`optimizationLevel`,
`firstPhasePreProcess` appeared in 7.x) and have no patched release, so
`npm audit` still lists them. A Linux `--dir` build packed the same asar
listing under 10 and 11. Drop the overrides once electron-builder depends on
11 itself.

CI does this automatically: pushing a `v<version>` tag runs
[release-desktop.yml](../../.github/workflows/release-desktop.yml), which
packages macOS (Apple silicon), Windows, and Linux installers and attaches them
to that tag's GitHub release, alongside the server image's and the extension
packages' own artifacts from `ci.yml`. The macOS build produces a `.zip` beside
the `.dmg` because an update installs from the zip — Squirrel.Mac cannot use a
disk image. Builds are unsigned unless the Apple signing/notarization secrets
are configured; a manual `workflow_dispatch` run uploads the installers as
workflow artifacts without creating a release.

The installers are named explicitly in `electron-builder.config.cjs` rather than
from `productName`, and carry a `generic` electron-updater feed rather than the
`github` provider. Both are load-bearing; [docs/releases.md](../../docs/releases.md)
explains why.

Notarization is a step of its own in that workflow, not electron-builder's
(`mac.notarize` is `false`), so a local `npm run dist` signs but doesn't
notarize. On macOS, CI runs three steps:

1. **Package** builds and signs the `.app` (`--dir`).
2. **Notarize** submits the app with `notarytool`, staples the ticket, and
   checks that Gatekeeper accepts it.
3. **Build disk image** wraps the stapled app (`--prepackaged`).

electron-builder's built-in notarization ran inside the packaging step and
printed nothing while it waited. A slow Apple queue (15–25 minutes on some
days) therefore looked like a hung codesign, and the runner kept waiting for as
long as Apple took. The Notarize step logs the submission id and Apple's status
as it goes, and stops waiting after 40 minutes. Apple keeps processing the
submission after a timeout or a cancelled run. To check one:

```sh
xcrun notarytool info <submission-id> --keychain-profile <profile>
```

### Mac App Store build

The same app also builds for the Mac App Store, for now only as a private
TestFlight preview. Apple treats it as a different app from the download:

- Its bundle id is the iOS app's, `com.hunterahlquist.solo` (and
  `com.hunterahlquist.solo.preview`), so the iPad and Mac apps are one App
  Store Connect app. The download keeps `com.solo.desktop`; changing that would
  strand its settings and its update feed.
- It runs in the App Sandbox instead of the hardened runtime
  (`build/entitlements.mas.plist` lists what it opens up and why). Settings
  live in the sandbox container,
  `~/Library/Containers/com.hunterahlquist.solo/Data/Library/Application Support/`,
  so the two apps never share a profile.
- electron-builder packs it with Electron's own Mac App Store build, which has
  no auto-updater. `process.mas` is true there, so the app drops
  *Check for Updates…* and leaves updates to TestFlight.

A `preview-v*` tag runs [release-mac-store.yml](../../.github/workflows/release-mac-store.yml),
which builds the signed `.pkg` and uploads it to TestFlight next to the iOS
build, where only internal testers can install it. It skips itself until the
`testflight` environment has:

| Name | Kind | What it is |
|---|---|---|
| `APP_STORE_CONNECT_KEY`, `_KEY_ID`, `_ISSUER_ID` | secrets | The App Store Connect API key the iOS upload already uses |
| `MAS_CERTIFICATES` | secret | One base64 `.p12` holding both the **Apple Distribution** and the **Mac Installer Distribution** certificate with their keys |
| `MAS_CERTIFICATES_PASSWORD` | secret | That `.p12`'s password |
| `MAS_PREVIEW_PROVISIONING_PROFILE` | secret | A base64 Mac App Store distribution profile for `com.hunterahlquist.solo.preview` |
| `SOLO_PREVIEW_ASC_APP_ID` | variable | The preview app's **Apple ID** from App Store Connect → App Information |

To build one locally, with those certificates in the login keychain:

```sh
npm run build:native
SOLO_MAS_PROVISIONING_PROFILE=~/Downloads/Solo_Preview_MAS.provisionprofile \
  npx electron-builder --config electron-builder.config.cjs --mac mas --arm64 --publish never
```

A distribution-signed app only opens once it is installed from TestFlight or
the store. To try one straight from `dist/`, build `--mac mas-dev` with an
Apple Development certificate and a development profile that lists your Mac.

### Native canvas rotation

On macOS the shell forwards Electron's [native rotation event](https://www.electronjs.org/docs/latest/api/browser-window#event-rotate-gesture-macos)
as a `solo:rotate-gesture` DOM event. Its detail contains the incremental
counterclockwise `rotation` in degrees, plus zoom-adjusted `clientX`/`clientY`.
A zero rotation marks the gesture end. A canvas can subscribe while mounted and
remove its listener on disposal; the bridge exposes no IPC or Electron objects
to the page. Run `npm run test:rotation-gesture` to check the window/preload path.
