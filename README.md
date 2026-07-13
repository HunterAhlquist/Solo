# AI Usage Disclaimer

<img width="376" height="214" alt="proompt" src="https://github.com/user-attachments/assets/046f15ed-2b9f-4b83-9b7c-2ed5b2ff2ac4" />

This entire project is slopped up by my lazy ass because I hate Jira so much. 

I freaking love PROOMPTING, I PROOMPT every day.
- 10 billion lines of code
- 10 billion proompts
- 10 billion security vulnerability
- 10 billion gallons of water GONE

AI is melting my programmer brain, please take away my Claude(TM) Pro(TM) subscription, I am begging you.


Le epic claug face:

<img width="350" height="350" alt="image" src="https://github.com/user-attachments/assets/d1b33d69-bca9-4b2d-b283-2d15140a3248" />

Nerd emoji lookin ahh

# Solo

A simple project management tool for small, trusted game-dev teams: Kanban boards with epics/tasks/sub-tasks, single-provider OIDC SSO, live editing over SignalR, and full audit logging.

**Stack:** ASP.NET Core Web API · EF Core + PostgreSQL · SignalR · OIDC (Google, Microsoft Entra, or any compliant issuer). The Blazor WASM client is served by the same host — one container + Postgres is the whole deployment. Design details live in [docs/backend-plan.md](docs/backend-plan.md).

## Development

Requires the .NET 10 SDK. Docker is needed for the integration tests (Testcontainers Postgres) — without it they are skipped.

```sh
dotnet build Solo.slnx
dotnet test Solo.slnx
```

Solution layout:

```
src/Solo.Api      ASP.NET Core host: controllers, SignalR hub, auth, serves the WASM client
src/Solo.Data     EF Core DbContext, entities, migrations, audit interceptor
src/Solo.Shared   DTOs, enums, hub contracts (shared with the WASM client)
tests/            unit (Solo.Data.Tests) + integration (Solo.Api.Tests)
```

Migrations are applied automatically on startup. To add one:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Solo.Data --startup-project src/Solo.Data
```

## CI & image publishing

[.github/workflows/ci.yml](.github/workflows/ci.yml) builds and tests every push/PR. Pushes to `main` (and `v*` tags) additionally publish the container image to **`ghcr.io/hunterahlquist/solo`** using the workflow's `GITHUB_TOKEN` — no extra secrets needed.

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
| `Database__AutoMigrate` | Apply migrations on startup (default `true`) |

Health endpoint: `GET /healthz` (checks the database connection).
