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

## Sign-in

Google refuses to run its sign-in pages inside Electron ("this browser or app
may not be secure"), so the shell signs in through the **system browser**: the
in-app **Authorize in browser** button opens a shell-served page on the local
origin (`http://localhost:41321/desktop-auth/signin`) in your default
browser, which completes the ordinary Google Identity Services flow and hands
the ID token back to the app. Same OAuth client, same token, no API changes.
The hand-off is protected by a single-use, expiring nonce minted per click.
(The client detects the shell via `window.soloDesktop`, exposed by the
preload, and swaps the GIS widget for the plain button — Google's widget
never loads inside the app.)

Provider registration:

- **Google:** add `http://localhost:41321` to the OAuth client's
  **Authorized JavaScript origins** (Google explicitly allows localhost
  origins on Web application clients) — the browser sign-in page runs on
  that origin.
- **Other OIDC (code + PKCE redirect):** add
  `http://localhost:41321/authentication/login-callback` as a redirect URI.
  These providers redirect the whole window rather than a popup, so they run
  in-app; whether that works depends on the provider's webview policy.

After the browser flow completes, the client trades the Google credential for
a **long-lived Solo session token** (`POST api/auth/sessions`; sliding
expiry, default 30 days via `Auth:SessionDays`), so the desktop app stays
signed in across restarts — no hourly re-auth. Signing out revokes the
session server-side. Requires an instance running an API version with the
sessions endpoint; on older instances desktop sign-in fails at the exchange
step.

## Packaging

```sh
npm run dist   # electron-builder → dist/ (dmg/zip, nsis, AppImage)
```
