# Solo — Frontend Construction Plan

> **Status (2026-07-13):** implemented through phase 7, with one exception — the **Playwright E2E suite has not been built**; bUnit component tests (`tests/Solo.Client.Tests`) run in CI instead. Notable deltas from the plan:
>
> - The **Electron shell anticipated in §3/§12 now exists** (`src/Solo.Desktop`). It took a different shape than sketched: instead of CORS + a desktop OIDC redirect, the shell serves the published client from a local origin and reverse-proxies to the configured instance (same-origin everywhere), and signs in through the **system browser**, exchanging the provider credential for a long-lived Solo session token. It can also expose a localhost MCP endpoint for AI agents. See [../src/Solo.Desktop/README.md](../src/Solo.Desktop/README.md).
> - Client routes use **project/board aliases** instead of GUIDs (`/projects/{alias}/boards/{alias}`), and tasks carry **serial numbers** (`PROJECT-BOARD-N`) shown on cards and usable as deep links to a task.
> - Projects, boards, and labels support **custom image icons** in addition to Material icon names.

Companion to `backend-plan.md`. The backend is fully implemented (API controllers, `BoardHub`, typed contracts in `Solo.Shared`), and `Solo.Api` already serves static files with an `index.html` fallback — the Dockerfile anticipates the published WASM client landing in its `wwwroot`. This plan covers building `Solo.Client` against that finished surface.

**Frontend stack:** Blazor WASM (.NET 10) · MudBlazor · MudBlazor.Markdown · SignalR client · `Microsoft.AspNetCore.Components.WebAssembly.Authentication` (OIDC code + PKCE).

---

## 1. Project structure & hosting

```
src/Solo.Client/
├── Program.cs              — auth bootstrap, DI, MudBlazor services
├── App.razor, Routes       — router + auth guards
├── Layout/                 — MainLayout, NavMenu (project → board tree), AppBar (presence-aware)
├── Auth/                   — dynamic OIDC config, ID-token message handler
├── Api/                    — typed ApiClient over HttpClient (one method per endpoint)
├── Realtime/               — BoardHubClient (typed SignalR wrapper), presence state
├── State/                  — BoardStore, ProjectNavStore + per-page state containers
├── Pages/                  — routable pages (see §6)
└── Components/             — Kanban pieces, markdown editor, dialogs, shared bits
```

- `Solo.Client` references `Solo.Shared` — **DTOs, enums, and the hub contract are consumed directly**; no client-side model duplication, no codegen.
- Hosted-WASM wiring: `Solo.Api` gets a `ProjectReference` to `Solo.Client` plus `Microsoft.AspNetCore.Components.WebAssembly.Server` and `app.UseBlazorFrameworkFiles()` ahead of the existing `UseStaticFiles()`/`MapFallbackToFile("index.html")`. Same origin ⇒ no CORS; `dotnet publish` on the Api keeps producing the single deployable image unchanged.
- Dev loop: `dotnet watch` on `Solo.Api` serves the client with hot reload; one process, real API underneath.

## 2. Authentication flow

- **Dynamic provider config:** WASM `Program.Main` is async — before building the host, fetch the anonymous `GET /api/config/auth` (`AuthConfigDto`) with a bare `HttpClient` and feed `Authority`/`ClientId` into `AddOidcAuthentication`. The provider is configured only in the API's environment, exactly as the backend plan intends.
- **Token wrinkle (important):** the API validates tokens with `ValidAudience = ClientId`, i.e. it expects the OIDC **ID token** (Google's access tokens are opaque, non-JWT). A custom `AuthorizationMessageHandler` attaches the stored ID token as the Bearer token on API calls, and the SignalR `AccessTokenProvider` returns the same.
- **Google does not do code + PKCE from a browser.** This plan originally assumed one OIDC flow served every provider; that is false. Google's *Web application* OAuth clients require a `client_secret` at the token endpoint **even with PKCE**, and a WASM client ships to the browser, so it can hold no secret. Google's *Desktop app* clients are public but only accept loopback redirects, so they can't serve a hosted web app either (they are, however, the right fit for the Electron shell in §12). Implicit (`response_type=id_token`) would dodge the token endpoint but Google no longer supports it for new integrations.
  - **Google** ⇒ **Google Identity Services** (`google.accounts.id`). The `credential` it returns *is* the ID token, with `aud = ClientId` — precisely what the API validates. No secret, no token endpoint, no redirect. Cost: Google-specific, ~1h token lifetime, and renewal via FedCM/One Tap rather than a silent iframe. The GIS script is loaded lazily, so non-Google instances never contact Google.
  - **Microsoft / generic OIDC** ⇒ `AddOidcAuthentication` with code + PKCE, exactly as described above.
  - `IdTokenProvider` is the only class that knows the difference; everything downstream — the message handler, the hub, the API — sees an ID token either way.
- **Route guards:** `AuthorizeRouteView` everywhere except sign-in and the setup wizard. If `AuthConfigDto.SetupCompleted` is false, all navigation redirects to `/setup` — the flag arrives on the anonymous config call, so the redirect needs no auth.
- After sign-in, `GET /api/users/me` hydrates the current user (id for presence/self-exclusion, `IsSuperAdmin` for gating admin UI).

## 3. API access & save-event semantics

- A single typed `ApiClient` wraps `HttpClient` with one method per endpoint, returning `Solo.Shared` DTOs. All mutations send the DTO's `Version` (xmin) for optimistic concurrency.
- **Configurable instance address:** every request — REST, the SignalR hub, and the anonymous auth-config fetch — resolves its base URL from one `InstanceConnection` service. The hosted web build defaults it to the page origin; a future **Electron shell** points it at a self-hosted instance instead (exactly one instance at a time — no multi-instance switching). Nothing else in the client may assume same-origin. Electron would additionally need CORS on the API and a desktop OIDC redirect flow; both deferred until that build exists, but this seam is what makes them cheap.
- **Save events, per the concept:** edits commit on blur / dropdown selection / dialog close — never per keystroke. Components keep local edit state and call the ApiClient on the save trigger, applying the response optimistically to the store.
- **Conflict handling:** a `409` means someone saved first — refetch the entity, replace local state, and show a snackbar ("Updated by someone else — your view was refreshed"). With presence warnings (§4) this should be rare.
- Every request carries the current hub connection id in `X-Connection-Id` so the server excludes this client from its own change broadcasts — no echo, no double-apply.

## 4. Realtime: change feed & presence

- `BoardHubClient` wraps `HubConnection` against `/hubs/board` using the shared `IBoardHubServer`/`IBoardClient` contract: `JoinBoard` on board-page entry, `LeaveBoard`/`SetPresence(None)` on exit, automatic reconnect with rejoin.
- **Change events carry field names, not values** (`EntityChangedEvent.ChangedFields`), so the client refetches on receipt: `task` events → `GET /api/tasks/{id}`; anything else (epic, label, board, comment) → a **debounced full `GET /api/boards/{id}`** refetch (`BoardDetailDto` is the one-shot render payload, so this is cheap and always consistent). Optimize to finer-grained patching only if it ever feels slow.
- **Change pulse:** remote changes apply silently — no toasts — but the affected card or drawer section flashes its outline once (a ~600 ms theme-colored CSS pulse) as the update lands, so activity is visible without being noisy.
- **Presence UX:** `PresenceChanged` snapshots drive avatar clusters on board cards and detail drawers — grey avatar = viewing, colored/pulsing = editing. Opening an editor calls `SetPresence(entityType, id, Editing)`; if someone else is already editing, the editor shows a prominent "Hunter is editing this" warning before you type, which is the concept's collision-avoidance mechanism.
- Store layer: a scoped `BoardStore` holds `BoardDetailDto` + presence snapshot, exposes `IObservable`-style change notifications (plain C# events are fine); components subscribe and re-render. No Fluxor — the store is small and the save-event model keeps mutation paths few.

## 5. Navigation sidebar

The drawer is the app's map. `DrawerVariant.Responsive` (not `Mini` — a mini drawer is icon-only and cannot hold a tree), open by default above the `Md` breakpoint.

```
All projects
├─ ▸ Solo                     ← MudNavGroup, collapsed
├─ ▾ Side quest               ← expanded
│    ├─ Main board            ← open boards only
│    ├─ Prototype
│    ├─ Project settings      ← members, label defaults, audit, closed boards
│    └─ + Add board           ← foot of this project's boards
└─ ─────────────────
   + Add project              ← pinned to the foot of the sidebar
   Instance settings
```

- **`ProjectNavStore`** holds the tree: projects from `GET /api/projects`, and a project's boards from `GET /api/projects/{id}/boards` **fetched on first expand**. Signing in costs one request, not one per project. No new API surface (see `backend-plan.md` §4).
- **Only open boards are listed.** Closed ones stay in the project home's faded foldout — "fold, don't hide" (§12) means reachable, not omnipresent. A sidebar listing every closed board buries the boards in play.
- **Create where the thing lives:** "Add board" sits at the foot of a project's boards; "Add project" is pinned to the foot of the sidebar, below the scrolling tree, so it stays reachable at any project count. Both open the shared `NameIconDialog` and navigate to what they created.
- **Affordances match the API's rules**, so no button 403s: "Add project" is super-admin only; "Add board", like a board's Close and Reopen, is super-admin only too, while a board's settings and labels follow the project's write rule (owner or `ReadWrite`, `ProjectNavStore.CanWrite`). A project's name is a group header rather than a link, so "Project settings" is how the tree reaches the project home.
- Mutations elsewhere keep the tree honest: the projects list, boards grid and project settings tab call `RefreshProjectsAsync`/`RefreshBoardsAsync` after create, rename, delete, close and reopen.

## 6. Pages

| Route | Page | Notes |
|---|---|---|
| `/signin` | Sign-in | Provider button (label/icon from `AuthConfigDto.Provider`) |
| `/setup` | First-run wizard | Stepper: static-credential check (`POST /api/setup/verify`) → retention (`PUT /api/setup/settings`) → SSO sign-in → handoff (`POST /api/setup/complete` re-proving static creds) |
| `/` | Projects list | Cards with icon + role badge; create (super admin only). The sidebar (§5) is the primary navigation; this page stays as the overview and the empty-state entry point |
| `/projects/{id}` | Project home | Boards grid — closed boards tucked in a faded foldout with one-click reopen — + tabs: Members, Label defaults, Audit, Settings |
| `/projects/{id}/boards/{boardId}` | **Kanban board** | The core surface (§7) |
| `/settings` | Instance settings | Super admin: audit retention (`/api/settings`) |

Project tabs: **Members** (invite by email, role toggle Read/ReadWrite, owner flag, pending badge), **Label defaults** (the task/subtask labels new boards are stamped from, color picker; a board's own labels are edited from the board itself), **Audit** (paged `MudTable` over `/api/projects/{id}/audit` with user/table/date filters).

## 7. Kanban board layout

Three fixed status columns — **Unstarted / In Progress / Finished** — with **epics as horizontal swimlanes** spanning all three, matching the decided "epic visually surrounds its child tasks":

```
              Unstarted     In Progress     Finished
┌ Epic A ──────────────────────────────────────────┐
│  [task] [task]   [task]           [task]         │
└───────────────────────────────────────────────────┘
┌ Epic B ──────────────────────────────────────────┐
│  [task]          [task] [task]                   │
└───────────────────────────────────────────────────┘
  Ad-hoc band (epic-less tasks)
   [task]           [task]           [task]
```

- **Task cards:** name, label chip (color from `LabelDto`), assignee avatars, sub-task progress ring (n/m), presence badge.
- **Drag & drop:** within a column = re-rank (`PUT rank` with `PrecedingId`/`FollowingId` — the server computes the fractional rank); across columns = `PUT status`; across swimlanes = `PUT epic` (into the ad-hoc band = null). Start with `MudDropContainer`; if nested swimlane × column zones fight it, fall back to a small JS-interop drag layer — isolate DnD behind one component so the swap is contained.
- **Auto-status tasks** (those with sub-tasks) refuse status drags with a shake + tooltip ("status follows sub-tasks"); the server recomputes their state from checklist changes.
- **Board labels:** the Labels button on a board's card in the project's board listing opens that board's own task/subtask labels — the only ones its items can carry — with a one-click reset back to the project defaults.
- **Epic bands** have name, assignee avatars, collapse toggle, and drag-to-reorder among bands (epic re-rank).
- **Finished-column hygiene:** past 25 cards the Finished column folds the overflow behind a faded expander ("Show all N") — same pattern as closed boards: tucked away, never locked away.
- **Detail drawer** (right side, epic or task): markdown description, label select, assignees, sub-task checklist (add/toggle/re-rank/label), comments thread. Sub-task toggles update task status locally via the same rollup rule for instant feedback, then reconcile from the server response.

## 8. Markdown editing

`MudBlazor.Markdown` renders (`MudMarkdown`); editing is a **write/preview toggle** — `MudTextField` textarea with monospace font, preview tab rendering the draft. Save on drawer close or explicit save button (a "save event"), Esc cancels. Same component reused for descriptions and comments.

## 9. Theming & UX defaults

- MudBlazor theme with dark mode as the default (game-dev audience), light theme available, preference persisted in `localStorage`.
- Icons: Material icon picker for project/board icons (they're free-text strings in the DTOs — store icon names).
- Density: compact — this is a tool, not a marketing site. Keyboard: Esc closes drawers, Enter commits single-line edits.
- Empty states with a single obvious action ("No boards yet — create one").

## 10. Testing

- **bUnit** component tests: status-rollup display, save-event triggers (blur commits, keystrokes don't), 409-refresh flow, presence badge rendering, drag handlers calling the right endpoint per drop zone.
- **Playwright E2E** (against the real API + Testcontainers Postgres, reusing the backend test-auth handler): sign-in redirect, setup wizard happy path, create project→board→epic→task, drag across columns, two-browser live-update and presence test.
- CI: client build + bUnit in the existing workflow; E2E as a separate job.

## 11. Decisions

All open questions were resolved on 2026-07-07:

- **Fold, don't hide:** closed boards live in a faded foldout on the project home with one-click reopen; the Finished column collapses overflow past 25 cards behind the same faded-expander pattern. Everything stays reachable.
- **Comments are a flat chronological thread**, markdown-enabled via the shared editor.
- **Desktop-first; Electron client anticipated.** No phone layout for v1. All backend connectivity goes through a single configurable instance address (`InstanceConnection`, §3) so the same client can later ship as an Electron app pointed at a self-hosted server — one instance at a time, never a multi-instance switcher.
- **Remote changes apply silently with a one-shot colored outline pulse** on the changed element — no toasts; presence avatars carry the ambient signal.
- **The sidebar is the navigation surface** (§5): a project → board tree with creation actions at the foot of each scope. Boards load per project on expand rather than up front, and closed boards are omitted from it — consistent with "fold, don't hide", since the project home still lists them.
