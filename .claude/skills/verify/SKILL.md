---
name: verify
description: Build, launch and drive the Solo app locally to verify a change end-to-end.
---

# Verifying Solo changes locally

## Launch

- Dev Postgres: `docker start solo-pg` (container on port 55432; connection
  string and Google auth config live in `dotnet user-secrets` for Solo.Api).
- Server: `dotnet run --project src/Solo.Api --launch-profile http` serves the
  API **and** the Blazor WASM client at `http://localhost:5144`.
  `.claude/launch.json` has a `solo-api` entry for the browser-pane
  `preview_start` tool.

## Getting a signed-in browser session (no Google round-trip)

The web client only sends Google ID tokens; the `solo_` session-token path is
desktop-only, gated on `window.soloDesktop` existing **at Blazor boot**
(`GoogleIdentityService` caches the check). Recipe:

1. Mint a session row (any existing user in `"Users"`):
   `TOKEN="solo_$(openssl rand -hex 24)"`, hash with
   `printf %s "$TOKEN" | shasum -a 256`, INSERT into `"AuthSessions"`
   (`TokenHash`, `UserId`, timestamps, short `ExpiresAt`, a recognizable
   `Description` for later cleanup).
2. Temporarily add the shim as an **external** file — the CSP in
   `src/Solo.Api/SecurityHeaders.cs` is
   `script-src 'self' 'wasm-unsafe-eval' https://accounts.google.com` with no
   `'unsafe-inline'`, so an inline `<script>` is blocked and `window.soloDesktop`
   is never set. It fails silently: the app just sits on the sign-in page and the
   only clue is a CSP violation in the browser console. `'self'` allows a file
   under `wwwroot`, so write
   `src/Solo.Client/wwwroot/js/dev-desktop.js`:
   `if (localStorage.getItem('solo.dev.desktop')) window.soloDesktop = { beginSignIn: () => {} };`
   and reference it from `src/Solo.Client/wwwroot/index.html` *before* the other
   scripts: `<script src="js/dev-desktop.js"></script>`
   (injecting after page load loses the race — the flag must exist at boot).
   **Remove both the file and the index.html line when done; neither may be
   committed.**
3. In the browser pane, set localStorage on the app origin:
   `solo.dev.desktop=1`, `solo.session.token=<TOKEN>`,
   `solo.session.user=<JSON of /api/users/me shape>`, then reload.
4. Clean up after: delete the `AuthSessions` row, remove the localStorage
   keys, delete `js/dev-desktop.js` and revert index.html, and undo any data
   edits made while driving.

## Driving gotchas

- Coordinate clicks from screenshots don't land reliably (screenshot is 1.25×
  the 1280×720 viewport); use `read_page` refs or element `.click()` via
  javascript_tool.
- Task cards and the description read-box aren't in the interactive
  accessibility tree; click them via `.kanban-card-slot` descendants /
  `.markdown-read`.
- The drawer overlay is `.mud-overlay`; drawer content is inside `.mud-drawer`.
- Direct board/task URL: `/projects/{PROJ}/boards/{BOARD}?task={N}`.
- Restart the server after editing `index.html` — Solo.Api serves the client's
  build output, so the edit is not picked up live.
- Dragging is pointer-event driven (`wwwroot/js/drag.js`), so `left_click_drag`
  works for a mouse drag and exercises the whole path through to the API. For a
  touch drag, dispatch `PointerEvent`s with `pointerType: 'touch'` from
  javascript_tool: `pointerdown`, then a `pointermove` after the 320 ms hold has
  elapsed (a tool round trip is long enough), then `pointerup`.
- The browser pane reports `document.hidden === true`, which throttles timers and
  freezes `requestAnimationFrame` and smooth scrolling. The drag paints from a
  rAF loop, so nothing updates between calls unless a screenshot forces a frame —
  interleave `computer{action:"screenshot"}` between steps, and don't read
  anything timing-dependent (the column-step dwell, a magnetic scroll landing)
  without one.
