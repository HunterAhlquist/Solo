<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/readme-header-dark.png">
  <img alt="Solo — project management for small game-dev teams" src="docs/images/readme-header-light.png">
</picture>

[![Desktop Release](https://github.com/HunterAhlquist/Solo/actions/workflows/release-desktop.yml/badge.svg)](https://github.com/HunterAhlquist/Solo/actions/workflows/release-desktop.yml) [![Server Release](https://github.com/HunterAhlquist/Solo/actions/workflows/ci.yml/badge.svg)](https://github.com/HunterAhlquist/Solo/actions/workflows/ci.yml)

A simple project management tool for small teams: Kanban boards with epics/tasks/sub-tasks as swim-lanes, single-provider OIDC SSO, live editing over SignalR, full audit logging, themes, and extension support.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/screenshots/board-dark.png">
  <img alt="A Solo board for the fictional game Regulatory Capture Quest: epic groups and epics as swim lanes across Unstarted, In Progress and Finished columns, with labels, deadline rings, sub-task progress and assignee avatars" src="docs/images/screenshots/board-light.png">
</picture>

First-party extensions, included in the server release downloads:
- MCP
- Roadmaps

**Stack:** ASP.NET Core Web API · EF Core + PostgreSQL · SignalR · OIDC (Google, Microsoft Entra, or any compliant issuer). The Blazor WASM client is served by the same host — one container + Postgres is the whole deployment.

No issue reporting, or public pull requesting. I tailored this to my own needs and just wanted to share it with the world if someone else can get use out of it. 
If you REALLY need to tell me about a problem security related, you can reach me on Bluesky: hunter.ahlquist.me
Otherwise you can fork it and make it infinitely better than I could ever.

<details>

<summary>AI Usage Disclaimer (Unhinged Alert)</summary>

<img width="376" height="214" alt="proompt" src="https://github.com/user-attachments/assets/046f15ed-2b9f-4b83-9b7c-2ed5b2ff2ac4" />

This entire project is slopped up by my lazy ass because I hate Jira so much.

I freaking love PROOMPTING, I PROOMPT every day.
- 10 billion lines of code
- 10 billion proompts
- 10 billion security vulnerability
- 10 billion gallons of water GONE

AI is melting my programmer brain, please take away my Claude(TM) Pro(TM) subscription, I am begging you.

### Le epic claug face:

<img width="350" height="350" alt="image" src="https://github.com/user-attachments/assets/d1b33d69-bca9-4b2d-b283-2d15140a3248" />

Nerd emoji lookin ahh

### Picture of Anthropic scanning books for their AI models:

<img width="330" height="262" alt="image" src="https://github.com/user-attachments/assets/9e0a962d-faf3-4bcb-aada-1de1d8485239" />

</details>

## Support
I dogfood this app by project managing my game: [The Dreamer Must Die](https://store.steampowered.com/app/2116590/The_Dreamer_Must_Die/?beta=0)
I don't vibe-code it unlike this project.

If you want to support me, give me a wishlist and buy the game (when it's out.)

## Quick start

To run an instance you need a Docker host, an OIDC client id from your identity
provider, and an address people will reach the app at. Everything else is in the
compose file.

1. **Register an OIDC client.** In Google Cloud Console or Entra ID, create a
   client for a browser SPA (authorization code + PKCE) with your Solo address
   as the redirect URI. One provider per deployment, switching later is
   unsupported. Google: copy the **full** client id, the one ending in
   `.apps.googleusercontent.com`.
2. **Set up** the docker compose:

   Here is an example that should get you started, though I recommend you use a secrets manager for the passwords.
   ```yaml
      services:
      solo:
         image: ghcr.io/hunterahlquist/solo:latest
         restart: unless-stopped
         depends_on:
            postgres:
            condition: service_healthy
         ports:
            - "30080:8080"
         environment:
            ConnectionStrings__Default: "Host=postgres;Port=5432;Database=solo;Username=solo;Password=CHANGE_ME_DB_PASSWORD"

            # Example for google auth, ClientId is that .apps.googleusercontent.com domain you were given.
            Auth__Authority: "https://accounts.google.com"
            Auth__ClientId: "CHANGE_ME_OIDC_CLIENT_ID"
            Auth__Provider: "google"

            # -- First-run setup account -------------------------------------------
            # Used only by the setup wizard; permanently disabled after the wizard
            # hands super admin off to your SSO account. Until that handoff these
            # credentials are the way in, and only a per-IP rate limit stands in
            # front of them — make the password a long random one.
            Setup__Username: "admin"
            Setup__Password: "CHANGE_ME_SETUP_PASSWORD"

         volumes:
            - solo-themes:/data/themes
            - solo-extensions:/data/extensions

         healthcheck:
            test: ["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080"]
            interval: 30s
            timeout: 5s
            retries: 3
            start_period: 30s

      postgres:
         image: postgres:17
         restart: unless-stopped
         environment:
            POSTGRES_DB: solo
            POSTGRES_USER: solo
            POSTGRES_PASSWORD: "CHANGE_ME_DB_PASSWORD"
         volumes:
            - solo-pgdata:/var/lib/postgresql/data
         healthcheck:
            test: ["CMD-SHELL", "pg_isready -U solo -d solo"]
            interval: 10s
            timeout: 5s
            retries: 5

      volumes:
      solo-pgdata:
      solo-themes:
      solo-extensions:
   ```
4. **Complete setup.** Browse to the app and sign in with the static
   `Setup__Username` / `Setup__Password` account. The wizard verifies SSO
   sign-in and sets audit retention; finish it while signed in with your own SSO
   account, which becomes the permanent super admin. The static account is
   disabled for good at that point.

From there, invite your team and hand out the URL. Optional extras: put it
behind a reverse proxy or the Cloudflare tunnel the compose file has commented
out, install the [desktop app](#desktop-app), add [themes](docs/themes.md), or
drop in [extensions](#extensions) such as the bundled MCP server.

>[!IMPORTANT]
>Everything after this is written by an LLM, sorry for the slop.

## Tour

The screenshots show a demo instance tracking *Regulatory Capture Quest*, a
turn-based RPG in which a scrappy open-weights lab fights its way to the capital
to stop OmniMind from passing the safety rules it wrote for everyone except
itself. The studio, its eight-person team and OmniMind's CEO, Chadwick
Moatsworth III, are all fictional.

### Tasks

Clicking a card opens the task in a drawer: a markdown description, labelled
sub-tasks, start and deadline dates, the comment thread, and the roadmaps the
task is on. Presence is live over SignalR. Here Hana has the task open while
Dmitri edits its description, so Priya gets a warning before saving over
Dmitri's work.
The **Work for an LLM** flag comes from the [MCP extension](#mcp-server-bundled-extension).

![The task drawer for RCQ-VS-6, "Phase 2: boss climbs a ladder and pulls it up behind him". Left: presence avatars, a warning that Dmitri Volkov is editing, the label, status, assignees, dates, a Roadmaps chip, the Work for an LLM checkbox and the markdown description. Right: five labelled sub-tasks, three done, and the team's comment thread](docs/images/screenshots/task-drawer.png)

### Audit log

Every changed column is recorded with who changed it, when, and the value it
replaced, and each project's log can be filtered by table, person and date. It
is how you find out who renamed Phase 3 to "boss simply becomes the regulator",
and who renamed it back.

![A project's audit log, newest first: Sasha marked the opt-out toggle task finished and renamed it, and Priya reverted Dmitri's rename of the Phase 3 task](docs/images/screenshots/audit.png)

### Themes

Three themes ship in the image: Ahlquist Interactive (the default, above),
Arcade and Blueprint. The super admin picks one for the whole instance, and
each person picks light or dark for themselves. To add your own, see
[docs/themes.md](docs/themes.md).

<table>
  <tr>
    <td width="50%"><img alt="The same board in the Arcade theme, dark: neon magenta and cyan on deep purple" src="docs/images/screenshots/theme-arcade.png"></td>
    <td width="50%"><img alt="The same board in the Blueprint theme, light: navy ink on a drafting grid" src="docs/images/screenshots/theme-blueprint.png"></td>
  </tr>
</table>

### On a phone

The same client, at phone width.

<p>
  <img width="260" alt="The Vertical Slice board on a phone, one epic lane at a time" src="docs/images/screenshots/mobile-board.png">
  &nbsp;
  <img width="260" alt="The task drawer full screen on a phone, with the editing warning and presence avatars" src="docs/images/screenshots/mobile-drawer.png">
</p>

## Extensions

Solo can be extended with packages that add API endpoints, database tables of
their own, hooks that intercept core write operations, and pages in the client.
How to write one: [docs/writing-extensions.md](docs/writing-extensions.md). Design and rationale: [docs/extension-plan.md](docs/extension-plan.md).

An extension package is a `.zip` — `extension.json` at the root, plus `server/`,
`client/` and `content/` — dropped into the extensions directory
(`/data/extensions` in the deployment template).

A package's **client half** is a Razor class library the browser downloads at
bootstrap and hands to the router, so its `@page` routes resolve like any other.
Its pages resolve MudBlazor, dialogs and JS interop from the host, and call the
API through `ISoloClientApi` — the host's own authenticated client, so an
extension shares the app's credential rather than inventing one. Use its
`ResolveExternal` for any address a person copies out of the app: in the desktop
shell the page's own origin is a local proxy, and only the instance's address
works in another tool. A manifest nav
entry whose route contains `{projectAlias}` renders per project, gated on the
viewer's role in it; one without it is instance-level and sits in the sidebar
footer.

> **Installing an extension is deploying code.** A server extension runs
> in-process with the database connection string, and a client extension runs in
> the same browser context as the session token. There is no sandbox and the
> manifest's declarations are not a security boundary. Trust a package exactly as
> much as you trust the image.

**Unlike themes, there is no rescan.** Controllers, hooks and DI are fixed once
the host is built, so installing, updating or removing an extension takes a
container restart. A package that fails to load is reported in Admin settings
and skipped; the instance still starts.

Each extension that stores data owns a Postgres **schema** (`ext_<id>`) inside
the existing database — not a separate database, so nothing extra to back up.
Its migrations run at startup after core's, gated on the same
`Database__AutoMigrate`. A failed migration disables that one extension (its
endpoints answer `503` naming the reason) rather than taking down the instance.

On a **first install** the log carries a `fail:` line from EF —
`SELECT ... FROM ext_<id>."__EFMigrationsHistory"` — immediately before the
migration succeeds. That is EF probing for a history table that does not exist
yet, exactly as core does against `public` on a first boot; it is not a failure.
The lines that report the real outcome are Solo's own, on either side of it:
`Extension '<id>': applying migrations to schema ext_<id>.` then
`Extension '<id>' migrated into schema ext_<id>.` A restart shows neither the
probe nor any `fail:` line.

Removing a package keeps its data, and Admin settings then lists the extension
as installed-but-absent — which is what makes it possible to offer a purge later
rather than leaving an orphan schema nobody can attribute. Purging drops the
schema *and* strips the extension's keys from core's per-entity field bags; it
is super-admin only, requires retyping the extension id, and is refused while
the extension is still loaded.

### MCP server (bundled extension)

One of the two extensions this repo ships is **`solo-mcp`** ([src/Solo.Mcp](src/Solo.Mcp)),
which lets an LLM client read and edit Solo's boards. It is a package like any
other, not part of the image: download `solo-mcp-<version>.zip` from the
[releases](../../releases), copy it into the extensions directory, and restart.
`build/pack-mcp-extension.sh` builds the same archive locally.

The package carries both halves: the server ([src/Solo.Mcp](src/Solo.Mcp)) and
the pages ([src/Solo.Mcp.Client](src/Solo.Mcp.Client)), whose **MCP access**
entry appears in the sidebar once the extension is installed. Each person
generates their own token there and pastes the connection snippet the page
produces into Claude Code, Claude Desktop, Cursor, VS Code or any other MCP
client. Claude Desktop gets a different snippet from the rest: its config file
launches local processes only and drops an entry carrying a URL, so the page
hands it an `mcp-remote` bridge instead of the HTTP shape. The endpoint is `POST /api/ext/mcp` — streamable HTTP, stateless, no
event stream — authenticated by `Authorization: Bearer solomcp_…`.

![The MCP access page: the signed-in person's token with its creation and last-used times and label, Regenerate and Revoke buttons, and a Connect a client card with a tab per client and the claude mcp add command for Claude Code](docs/images/screenshots/mcp.png)

A token **acts as the person who generated it**: every tool call re-enters
Solo's own REST API carrying that token, so it gets the same authorization,
auditing and live-board broadcasts as the same action performed in a browser,
and it reaches only the projects its owner is a member of. Tools address a task
by GUID or by full serial number (`DMD-PXW26-2`), so an agent holding a serial
needs no id discovery.

Tokens live in the extension's own `ext_mcp` schema and are stored only as
SHA-256 hashes, so nobody — super admin included — can read one back. One live
token per account: generating a new one blocks the old one immediately, and the
revoked row stays in the audit. A super admin
sees a second section on the same page: every token's owner, creation and
revocation times and a 24-hour usage graph, a per-row **Revoke** for cutting off
one person, and an emergency **Revoke all** for cutting off the instance.

### Roadmaps (bundled extension)

The other is **`solo-roadmap`** ([src/Solo.Roadmap](src/Solo.Roadmap)), which
adds timelines. A roadmap gathers tasks from any of a project's boards into one
row each, drawn as a bar from start date to deadline over a day grid with
sprint stripes. Dragging a bar reschedules the task itself, so the board and
the roadmap never disagree about a date. Milestones mark dates that belong to
the roadmap rather than to a task. Missed deadlines flash red and are listed
under the grid, which is how the eleven-minute battle theme got caught.
Whoever else has the roadmap open shows in the header.

Install it like the MCP package: `solo-roadmap-<version>.zip` from the
[releases](../../releases), or `build/pack-roadmap-extension.sh` locally. The
full feature list is in [its README](src/Solo.Roadmap/README.md).

![The Road to Next Fest roadmap: bars for tasks across the Vertical Slice, Art & Audio and Bugs boards, today's column highlighted, dotted milestone lines for the vertical slice playtest and the publisher pitch, the overdue battle-theme bar in red, and the Missed deadlines table below](docs/images/screenshots/roadmap.png)

## Desktop app

[src/Solo.Desktop](src/Solo.Desktop) is an Electron shell around the unmodified Solo web client. The shell serves the published Blazor WASM build from a local origin (`http://localhost:41321`) and reverse-proxies `/api/*` and `/hubs/*` (REST, images, SignalR WebSockets) to whichever Solo instance is configured in **Server Settings** — so the client runs byte-for-byte identical to the hosted build, same-origin, with no CORS anywhere and no changes to the API.

### Desktop Sign-in

Google refuses to run its sign-in pages inside Electron ("this browser or app may not be secure"), so the shell signs in through the **system browser** on the **instance's own origin**, then returns through a PKCE-bound loopback callback:

1. The in-app **Authorize in browser** button opens an ephemeral `127.0.0.1` listener, generates a PKCE verifier, and asks the instance to open a flow (`POST api/auth/desktop/start`) with that port and the verifier's challenge.
2. The shell opens `https://<instance>/desktop-auth?state=…` in your default browser. That page runs the ordinary Google Identity Services flow — on the origin the operator already registered for the web client, so **no extra provider registration is needed** — and sends a short-lived, one-time authorization code to the loopback listener.
3. The shell redeems that code with its verifier (`POST api/auth/desktop/redeem`) and receives a **long-lived Solo session token** (sliding expiry, default 30 days via `Auth__SessionDays`), exactly once. A remote client that started the flow never receives the loopback code, and an intercepted code is useless without the verifier.

Signing out revokes the session server-side. Both the desktop app and instance must support the secure loopback flow; incompatible versions fail closed, and the current desktop app identifies an old server response and asks for an update. (The client detects the shell via `window.soloDesktop`, exposed by the preload, and swaps the GIS widget for the plain button — Google's widget never loads inside the app.)

For **other OIDC providers** (code + PKCE redirect) the flow runs in-app: add `http://localhost:41321/authentication/login-callback` as a redirect URI. These providers redirect the whole window rather than a popup; whether that works depends on the provider's webview policy.

## Development

Requires the .NET 10 SDK. Docker is needed for the integration tests (Testcontainers Postgres) — without it they are skipped.

```sh
dotnet build Solo.slnx
dotnet test Solo.slnx
```

Solution layout:

```
src/Solo.Api           ASP.NET Core host: controllers, SignalR hub, auth, serves the WASM client
src/Solo.Data          EF Core DbContext, entities, migrations, audit interceptor
src/Solo.Shared        DTOs, enums, hub contracts (shared with the WASM client)
src/Solo.Extensibility the public contract extensions compile against (see Extensions)
src/Solo.Extensions    the extension host: package loading, hook dispatch, registry
tests/                 unit (Solo.Data.Tests) + integration (Solo.Api.Tests)
```

Migrations are applied automatically on startup. To add one:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Solo.Data --startup-project src/Solo.Data
```

### Running

```sh
cd src/Solo.Desktop
npm install
npm run build:client   # dotnet publish Solo.Client → ./wwwroot
npm start
```

On first launch a settings window asks for the instance address (e.g. `https://solo.example.com`). **Test** performs an anonymous fetch of `/api/config/auth` to confirm the address is a Solo instance before you save. Change it later via *Solo → Server Settings…* (`Cmd/Ctrl+,`). Settings live in `settings.json` under Electron's `userData` directory; the local port (default `41321`) can be changed there if it clashes.

During development, `SOLO_CLIENT_DIR=/path/to/publish/wwwroot npm start` serves client assets from anywhere without re-copying.

## CI & image publishing

[.github/workflows/ci.yml](.github/workflows/ci.yml) builds and tests every PR into `main`. Pushing a **`server-v*`** tag publishes the changed release artifacts using the workflow's `GITHUB_TOKEN` — no extra secrets needed. A changed container image goes to **`ghcr.io/hunterahlquist/solo`**; the tag is stripped to produce the image tags, so `server-v1.6.0` publishes `1.6.0`, `1.6`, `latest`, and `sha-…`, while a prerelease like `server-v1.6.0-rc1` skips the floating `1.6`.

The server image, MCP extension, and desktop client version independently — see [Packaging](#packaging) for the `desktop-v*` series. For a `server-v*` tag, CI compares the server inputs (`Dockerfile`, `.dockerignore`, and the server's project-reference closure) and MCP package inputs separately against the previous `server-v*` tag it descends from. It publishes only the artifacts whose inputs changed; test-, documentation-, desktop-, workflow-, and packaging-only changes do not republish the server, and server-only changes do not attach another copy of an unchanged MCP package. If neither artifact changed, CI creates no GitHub release and moves no GHCR tags.

### Packaging

```sh
npm run dist   # electron-builder → dist/ (arm64 dmg, nsis, AppImage)
```

CI does this automatically: pushing a **`desktop-v*`** tag runs [release-desktop.yml](.github/workflows/release-desktop.yml), which stamps `package.json` with the version from the tag, packages an Apple silicon macOS `.dmg` plus Windows and Linux installers, and attaches them to the GitHub release for the tag. Builds are unsigned unless the Apple signing/notarization secrets are configured; a manual `workflow_dispatch` run uploads the installers as workflow artifacts without creating a release.

The desktop client versions independently of the container image, which has its own `server-v*` series. A tag whose commit changes nothing the installers are built from (`src/Solo.Desktop`, `src/Solo.{Client,Shared}`, `Icons/`, this workflow) is skipped rather than repackaged, measured against the previous `desktop-v*` tag it descends from — so an API- or docs-only release costs nothing here.
