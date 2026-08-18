# Solo

A simple project management tool for small, trusted game-dev teams: Kanban boards with epics/tasks/sub-tasks, single-provider OIDC SSO, live editing over SignalR, and full audit logging.

**Stack:** ASP.NET Core Web API · EF Core + PostgreSQL · SignalR · OIDC (Google, Microsoft Entra, or any compliant issuer). The Blazor WASM client is served by the same host — one container + Postgres is the whole deployment. Design details live in [docs/backend-plan.md](docs/backend-plan.md).

<details>

<summary>AI Usage Disclaimer</summary>

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

## CI & image publishing

[.github/workflows/ci.yml](.github/workflows/ci.yml) builds and tests every PR into `main`. Pushing a **`server-v*`** tag additionally publishes the container image to **`ghcr.io/hunterahlquist/solo`** using the workflow's `GITHUB_TOKEN` — no extra secrets needed. The tag is stripped to produce the image tags, so `server-v1.6.0` publishes `1.6.0`, `1.6`, `latest`, and `sha-…`; a prerelease like `server-v1.6.0-rc1` skips the floating `1.6`.

The image and the desktop client version independently — see [Packaging](#packaging) for the `desktop-v*` series. A tag whose commit changes nothing the image is built from (`Dockerfile`, `src/Solo.{Api,Client,Data,Shared}`, `tests/`, this workflow) is skipped rather than rebuilt, measured against the previous `server-v*` tag it descends from.

> The first publish creates the GHCR package. Verify it is **private** afterwards (GitHub → your profile → Packages → solo → Package settings); packages default to private, but it's worth confirming before granting anyone pull access.

## Deployment (TrueNAS SCALE custom app)

The template compose file is [deploy/docker-compose.yml](deploy/docker-compose.yml) — app container + `postgres:17` + a data volume.

1. **Registry credential** (the image is private): TrueNAS → *Apps → Configuration → Manage Container Registries* → add `ghcr.io` with your GitHub username and a PAT that has `read:packages`.
2. **Install**: *Apps → Discover Apps → ⋮ → Install via YAML*, paste the template, and replace every `CHANGE_ME` value (DB password, OIDC client id, setup password). Pick the host port you want (default `30080`).
3. **OIDC app registration**: create a client with your provider (Google Cloud Console or Entra ID) for a browser SPA using authorization code + PKCE, with your Solo URL as the redirect URI, and put its client id in `Auth__ClientId`. One provider per deployment — switching later is unsupported.
4. **First-run setup**: browse to the app. The instance starts in setup mode; sign in with the static setup account (`Setup__Username` / `Setup__Password`), walk the wizard (verify SSO sign-in, set audit retention), and complete it while signed in with your SSO account. That account becomes the permanent super admin and the static account is disabled for good.

All configuration is environment variables:

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Default` | Postgres connection string |
| `Auth__Authority` | OIDC authority URL (e.g. `https://accounts.google.com`) |
| `Auth__ClientId` | OIDC client id; also the expected token audience |
| `Auth__Provider` | Claim-mapping preset: `google`, `microsoft`, or `generic` |
| `Setup__Username` / `Setup__Password` | Static first-run setup credentials (dead after handoff) |
| `Database__AutoMigrate` | Apply migrations on startup (default `true`); also gates extension migrations |
| `Themes__Directory` | Directory scanned for theme packages |
| `Extensions__Directory` | Directory scanned for extension packages |

Health endpoint: `GET /healthz` (checks the database connection).

## Extensions

Solo can be extended with packages that add API endpoints, database tables of
their own, hooks that intercept core write operations, and pages in the client.
Design and rationale are in [docs/extension-plan.md](docs/extension-plan.md).

An extension package is a `.zip` — `extension.json` at the root, plus `server/`,
`client/` and `content/` — dropped into the extensions directory
(`/data/extensions` in the deployment template).

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

Removing a package keeps its data, and Admin settings then lists the extension
as installed-but-absent — which is what makes it possible to offer a purge later
rather than leaving an orphan schema nobody can attribute. Purging drops the
schema *and* strips the extension's keys from core's per-entity field bags; it
is super-admin only, requires retyping the extension id, and is refused while
the extension is still loaded.

## Desktop app

[src/Solo.Desktop](src/Solo.Desktop) is an Electron shell around the unmodified Solo web client. The shell serves the published Blazor WASM build from a local origin (`http://localhost:41321`) and reverse-proxies `/api/*` and `/hubs/*` (REST, images, SignalR WebSockets) to whichever Solo instance is configured in **Server Settings** — so the client runs byte-for-byte identical to the hosted build, same-origin, with no CORS anywhere and no changes to the API.

### Running

```sh
cd src/Solo.Desktop
npm install
npm run build:client   # dotnet publish Solo.Client → ./wwwroot
npm start
```

On first launch a settings window asks for the instance address (e.g. `https://solo.example.com`). **Test** performs an anonymous fetch of `/api/config/auth` to confirm the address is a Solo instance before you save. Change it later via *Solo → Server Settings…* (`Cmd/Ctrl+,`). Settings live in `settings.json` under Electron's `userData` directory; the local port (default `41321`) can be changed there if it clashes.

During development, `SOLO_CLIENT_DIR=/path/to/publish/wwwroot npm start` serves client assets from anywhere without re-copying.

### Sign-in

Google refuses to run its sign-in pages inside Electron ("this browser or app may not be secure"), so the shell signs in through the **system browser** on the **instance's own origin** — device-code style:

1. The in-app **Authorize in browser** button asks the instance to open a flow (`POST api/auth/desktop/start`), which returns a `state` for the browser URL and a `deviceCode` that never leaves the shell.
2. The shell opens `https://<instance>/desktop-auth?state=…` in your default browser. That page runs the ordinary Google Identity Services flow — on the origin the operator already registered for the web client, so **no extra provider registration is needed** — and posts the credential back to the instance, which verifies it and pins the signed-in user to the flow (single-use, expires after 10 minutes).
3. The shell polls `POST api/auth/desktop/poll` with the `deviceCode` and receives a freshly minted **long-lived Solo session token** (sliding expiry, default 30 days via `Auth__SessionDays`), exactly once. The client stores it and boots into the signed-in state — no hourly re-auth, and the Google credential never touches the shell.

Signing out revokes the session server-side. Requires an instance running an API version with the desktop-auth endpoints; on older instances the sign-in fails at the start step with a message saying to update the server. (The client detects the shell via `window.soloDesktop`, exposed by the preload, and swaps the GIS widget for the plain button — Google's widget never loads inside the app.)

For **other OIDC providers** (code + PKCE redirect) the flow runs in-app: add `http://localhost:41321/authentication/login-callback` as a redirect URI. These providers redirect the whole window rather than a popup; whether that works depends on the provider's webview policy.

### MCP

**Off by default.** Enable **Allow AI agents (MCP)** in *Solo → Server Settings…* and the running app serves an MCP endpoint at `http://localhost:41321/mcp` (streamable HTTP, stateless), so an MCP client on the same machine — Claude Code, Claude Desktop — can browse projects/boards and create or edit tasks. Task tools address a task by GUID or by full serial number (`DMD-PXW26-2`), so an agent holding a serial needs no id discovery. The toggle applies on save without a restart; while off, `/mcp` answers 404. The tools call the configured instance's REST API with the **signed-in user's own session token**, read from the running client per request: no separate credential, and MCP access ends the moment you sign out or close the app.

Connect Claude Code with:

```sh
claude mcp add --transport http solo http://localhost:41321/mcp
```

(This repo's [.mcp.json](.mcp.json) already registers it for sessions inside the repo.) Only localhost clients can reach the endpoint: the local server rejects non-localhost `Host` headers, and `/mcp` additionally rejects browser cross-origin requests by `Origin`.

### Packaging

```sh
npm run dist   # electron-builder → dist/ (dmg/zip, nsis, AppImage)
```

CI does this automatically: pushing a **`desktop-v*`** tag runs [release-desktop.yml](.github/workflows/release-desktop.yml), which stamps `package.json` with the version from the tag, packages macOS (x64 + arm64), Windows, and Linux installers, and attaches them to the GitHub release for the tag. Builds are unsigned unless the Apple signing/notarization secrets are configured; a manual `workflow_dispatch` run uploads the installers as workflow artifacts without creating a release.

The desktop client versions independently of the container image, which has its own `server-v*` series. A tag whose commit changes nothing the installers are built from (`src/Solo.Desktop`, `src/Solo.{Client,Shared}`, `Icons/`, this workflow) is skipped rather than repackaged, measured against the previous `desktop-v*` tag it descends from — so an API- or docs-only release costs nothing here.
