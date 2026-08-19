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

## Window chrome

The window has no OS title bar. The platform draws its own controls over the
client's app bar instead — inset traffic lights on macOS, the Window Controls
Overlay on Windows and Linux (where the menu bar auto-hides behind `Alt`) — and
the app bar is the region you drag the window by.

That takes agreement from both sides: the preload puts `solo-desktop`,
`solo-desktop--mac` / `solo-desktop--overlay` and `solo-desktop--fullscreen` on
`<html>`, and the client's `app.css` keys its drag regions and its room for the
buttons off them (see the *Desktop shell chrome* block there). The classes exist
only inside the shell, so the hosted web build keeps an ordinary app bar.

## Sign-in

Google refuses to run its sign-in pages inside Electron ("this browser or app
may not be secure"), so the shell signs in through the **system browser** on
the **instance's own origin** — device-code style:

1. The in-app **Authorize in browser** button asks the instance to open a
   flow (`POST api/auth/desktop/start`), which returns a `state` for the
   browser URL and a `deviceCode` that never leaves the shell.
2. The shell opens `https://<instance>/desktop-auth?state=…` in your default
   browser. That page runs the ordinary Google Identity Services flow — on
   the origin the operator already registered for the web client, so **no
   extra provider registration is needed** — and posts the credential back
   to the instance, which verifies it and pins the signed-in user to the
   flow (single-use, expires after 10 minutes).
3. The shell polls `POST api/auth/desktop/poll` with the `deviceCode` and
   receives a freshly minted **long-lived Solo session token** (sliding
   expiry, default 30 days via `Auth:SessionDays`), exactly once. The client
   stores it and boots into the signed-in state — no hourly re-auth, and the
   Google credential never touches the shell.

Signing out revokes the session server-side. Requires an instance running an
API version with the desktop-auth endpoints; on older instances the sign-in
fails at the start step with a message saying to update the server. (The
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

CI does this automatically: pushing a `v*` tag runs
[release-desktop.yml](../../.github/workflows/release-desktop.yml), which
packages macOS (x64 + arm64), Windows, and Linux installers and attaches them
to the GitHub release for the tag. Builds are unsigned unless the Apple
signing/notarization secrets are configured; a manual `workflow_dispatch` run
uploads the installers as workflow artifacts without creating a release.
