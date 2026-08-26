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
app is running.

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
belongs: [src/Solo.Mcp](../Solo.Mcp) is an extension the server loads, so MCP
works from any machine and any client rather than only while this app is
running, and access is granted per user by a token the owner can rotate and an
admin can audit and revoke.

Install the package on the instance and generate a token from **MCP access** in
the profile menu — the page produces the config snippet each client wants. See
the [MCP server section](../../README.md#mcp-server-bundled-extension) of the
root README.

## Packaging

```sh
npm run dist   # electron-builder → dist/ (dmg/zip, nsis, AppImage)
```

electron-builder's `latest` dist-tag on npm sits at 26.15.3 while the newest
published 26.x is 26.15.7 (tagged `v26`), so `npm outdated` reports the pinned
version as *ahead* of latest and a bare `npm install electron-builder@latest`
would quietly downgrade it. The floor here is `^26.15.7` so the range itself
refuses the older build; `npm ci` follows the lockfile either way.

CI does this automatically: pushing a `v*` tag runs
[release-desktop.yml](../../.github/workflows/release-desktop.yml), which
packages macOS (x64 + arm64), Windows, and Linux installers and attaches them
to the GitHub release for the tag. Builds are unsigned unless the Apple
signing/notarization secrets are configured; a manual `workflow_dispatch` run
uploads the installers as workflow artifacts without creating a release.
