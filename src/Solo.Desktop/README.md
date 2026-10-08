# Solo Desktop

An Electron shell around the unmodified Solo web client. The shell serves the
published Blazor WASM build from a local origin (`http://localhost:41321`) and
reverse-proxies `/api/*` and `/hubs/*` (REST, images, SignalR WebSockets) to
whichever Solo instance is configured in **Server Settings** — so the client
runs byte-for-byte identical to the hosted build, same-origin, with no CORS
anywhere and no changes to the API.

## Running

```sh
cd src/Solo.Desktop
npm install
npm run build:client   # dotnet publish Solo.Client → ./wwwroot
npm start
```

On first launch a settings window asks for the instance address
(e.g. `https://solo.example.com`). **Test** performs an anonymous fetch of
`/api/config/auth` to confirm the address is a Solo instance before you save.
Change it later via *Solo → Server Settings…* (`Cmd/Ctrl+,`). Settings live in
`settings.json` under Electron's `userData` directory; the local port
(default `41321`) can be changed there if it clashes.

During development, `SOLO_CLIENT_DIR=/path/to/publish/wwwroot npm start`
serves client assets from anywhere without re-copying.

### Glass backdrop (macOS)

The main window uses a translucent backdrop by default while leaving the
settings window and non-macOS builds unchanged. On macOS 26 and later Solo
uses the native Liquid Glass module. If that module is unavailable, unsupported,
or cannot attach to the window, Solo falls back to Electron's AppKit vibrancy:

```sh
npm run build:client && npm start
```

To disable both treatments for troubleshooting, launch with
`SOLO_DISABLE_LIQUID_GLASS=1 npm start`.

The backdrop follows Solo's Light, Dark, or Match system preference rather than
independently inheriting macOS. Changes apply to the native material while the
app is running. The Blazor loading view uses the same theme-tinted material from
the first paint instead of covering the native backdrop with an opaque splash.

The Server Settings window uses the same native treatment. Its macOS native
window effects checkbox disables both Liquid Glass and the vibrancy fallback
after the next restart; the choice is stored with the other shell settings.

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
may not be secure"), so the shell signs in through the **system browser** on
the **instance's own origin**, then returns through a PKCE-bound loopback
callback:

1. The in-app **Authorize in browser** button opens an ephemeral `127.0.0.1`
   listener, generates a PKCE verifier, and starts a flow (`POST
   api/auth/desktop/start`) with that port and the verifier's challenge.
2. The shell opens `https://<instance>/desktop-auth?state=…` in your default
   browser. That page runs the ordinary Google Identity Services flow — on
   the origin the operator already registered for the web client, so **no
   extra provider registration is needed** — and posts the credential back
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

For **other OIDC providers** (code + PKCE redirect) the flow runs in-app:
add `http://localhost:41321/authentication/login-callback` as a redirect URI.
These providers redirect the whole window rather than a popup; whether that
works depends on the provider's webview policy.

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

- `build:client` publishes without the precompressed `.br`/`.gz` siblings.
  The local server only ever streams the plain file. Compressed bytes don't
  recompress, so the siblings took more of the image than the client they
  duplicate.
- `scripts/after-all-artifact-build.mjs` re-encodes the image as ULMO (lzma),
  which electron-builder's `dmg.format` doesn't offer. It is about a fifth
  smaller than the default zlib. `dmg.writeUpdateInfo` is off because there is
  no auto-updater to read a blockmap, and converting would invalidate it
  anyway.

electron-builder's `latest` dist-tag on npm sits at 26.15.3 while the newest
published 26.x is 26.15.7 (tagged `v26`), so `npm outdated` reports the pinned
version as *ahead* of latest and a bare `npm install electron-builder@latest`
would quietly downgrade it. The floor here is `^26.15.7` so the range itself
refuses the older build; `npm ci` follows the lockfile either way.

CI does this automatically: pushing a `desktop-v*` tag runs
[release-desktop.yml](../../.github/workflows/release-desktop.yml), which
packages macOS (Apple silicon), Windows, and Linux installers and attaches them
to the GitHub release for the tag. Builds are unsigned unless the Apple
signing/notarization secrets are configured; a manual `workflow_dispatch` run
uploads the installers as workflow artifacts without creating a release.

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
