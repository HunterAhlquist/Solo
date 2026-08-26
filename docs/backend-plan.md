# Solo — Backend Construction Plan

> **Status (2026-07-13):** implemented in full; this document is the historical design record. Shipped since it was written and therefore not covered below:
>
> - **Serial-number aliases** (`Solo.Shared/Aliases.cs`): every project and board carries a short `A-Z0-9` handle that stands in for its GUID in routes and composes user-facing task serials like `ABC-MAIN-7`.
> - **Image assets** (`/api/images`): small custom project/board/label icons stored inline in Postgres; anonymous reads keyed on the GUID.
> - **Session tokens & desktop sign-in** (`/api/auth/sessions`, `/api/auth/desktop/*`): long-lived sliding-expiry `solo_` tokens minted from a fresh OIDC credential, plus a browser-hand-off flow for the Electron shell (`src/Solo.Desktop`) — see [../src/Solo.Desktop/README.md](../src/Solo.Desktop/README.md).
> - **Rate limiting**: a global per-user/IP token bucket plus strict windows on credential-checking and desktop sign-in endpoints (`Solo.Api/RateLimiting.cs`, configured in `Program.cs`).
>
> The solution file is `Solo.slnx` (not `Solo.sln`), and the client planned as out of scope here now exists as `src/Solo.Client` — see [frontend-plan.md](frontend-plan.md). Deployment and configuration docs live in the root [README](../README.md).

Derived from `Core Concept.md` (Obsidian: Solo Project Management vault). Solo is a low-configuration project management tool for small, trusted game-dev teams: Kanban boards with epics/tasks/sub-tasks, single-provider OIDC SSO (Google by default), live editing over SignalR, and full audit logging.

**Backend stack:** ASP.NET Core Web API · EF Core + PostgreSQL (Npgsql) · SignalR · OIDC SSO (provider-configurable — Google, Microsoft, or any compliant issuer). The Blazor WASM + MudBlazor client consumes this API and is out of scope here, but shapes the DTO and hub contracts.

---

## 1. Solution structure

```
Solo.sln
├── src/
│   ├── Solo.Api        — ASP.NET Core host: controllers, SignalR hub, auth, DI, serves WASM static files
│   ├── Solo.Data       — EF Core DbContext, entities, migrations, audit interceptor, seeding
│   └── Solo.Shared     — DTOs, enums, hub contract interfaces (referenced by both Api and the WASM client)
└── tests/
    ├── Solo.Api.Tests  — integration tests (Testcontainers Postgres, WebApplicationFactory)
    └── Solo.Data.Tests — unit tests (status rollup, label scoping, audit writer)
```

One deployable unit: the API host also serves the published Blazor WASM app, so deployment is a single container + Postgres.

## 2. Data model

Hierarchy: **Project → Board → Epic → Task → SubTask**.

**Kanban columns (decided):** boards render three fixed columns — **Unstarted / In Progress / Finished** — derived from task status. No `Column` entity, no column configuration.

| Entity | Key fields | Notes |
|---|---|---|
| `User` | Issuer, Subject, Email, DisplayName, AvatarUrl, IsSuperAdmin | Provisioned on first SSO sign-in; identity is the OIDC `iss` + `sub` pair (provider-agnostic); `IsSuperAdmin` granted by the setup handoff |
| `Project` | Name, Icon | Owns access config and the label defaults new boards copy |
| `ProjectMember` | ProjectId, UserId, Role (`Read` \| `ReadWrite`), IsOwner | The whole access model — deliberately simple |
| `Board` | ProjectId, Name, Icon, IsOpen | Open/Close status per concept |
| `Epic` | BoardId, Name, Description (markdown), Rank | Renders as a visual container surrounding its child tasks on the board. Has comments + assignees |
| `Task` | BoardId, EpicId (nullable), Name, LabelId, Description (markdown), Status (`Unstarted` \| `InProgress` \| `Finished`), Rank | Null `EpicId` ⇒ ad-hoc task sitting directly on the board; when set, the epic must belong to the same board. Status drives column placement. With sub-tasks, status is auto-derived: all finished ⇒ `Finished`, some finished ⇒ `InProgress`, none ⇒ `Unstarted` |
| `SubTask` | TaskId, Name, LabelId, IsFinished, Rank | Checklist item |
| `Label` | ProjectId, BoardId (nullable), Kind (`Task` \| `SubTask`), Name, Color | `BoardId = null` ⇒ a project default: a template, never carried by an item; non-null ⇒ a board's own label, the only kind items can carry |
| `Comment` | AuthorId, Body (markdown), CreatedAt, EpicId? / TaskId? | One table, exactly one parent FK set (check constraint) |
| `Assignment` | UserId, EpicId? / TaskId? | Many-to-many user assignment |
| `AuditEntry` | Timestamp (UTC), UserId, TableName, RowId, ColumnName, PreviousValue | Written by SaveChanges interceptor |
| `InstanceSettings` | AuditRetentionDays, SetupCompleted | Single-row config table; retention is the only audit knob, per concept. `SetupCompleted` kills the static setup account |

Seeded label defaults on project creation, copied onto each board as it is created:
- Task labels: **Bug, Feature, Improvement, Feedback Session**
- SubTask labels: **Code, Asset, Design, Writing**

Conventions: `Guid` PKs, `xmin` optimistic-concurrency token on all editable entities (backstop for collisions the presence system doesn't prevent), cascade deletes down the hierarchy — except epic deletion, which detaches child tasks to ad-hoc (`EpicId` → null, `ON DELETE SET NULL`) rather than deleting them — and indexes on FKs and `AuditEntry(Timestamp)`.

`Rank` (epics, tasks, sub-tasks) tracks user-defined ordering. Use a gapped/fractional value (e.g., decimal midpoints with periodic rebalance) so a drag-and-drop reorder updates only the moved row instead of reindexing siblings.

## 3. Authentication & authorization

- **OIDC SSO (authorization code + PKCE)** performed by the WASM client against a **configurable provider**: the deployment sets an OIDC authority URL + client ID via environment config, and the API validates issued tokens with `AddJwtBearer` using the authority's discovery document — provider-agnostic by construction. **Google and Microsoft (Entra ID) are the supported presets**; a thin per-provider claim mapping absorbs the quirks (e.g., email arriving as `preferred_username` on Microsoft tokens). No password store, no token issuance of our own — simplest thing that works for a private instance.
- **Exactly one provider per deployment.** Changing it after setup is unsupported for now: identities are keyed on `iss` + `sub`, so a provider swap leaves existing accounts unmatched. Not enforced in code — a future **migration manager** could re-link accounts to a new provider, and each user row's stored `iss` already preserves the history such a tool would need.
- **Client config:** the WASM app fetches the authority + client ID from an anonymous `/api/config/auth` endpoint at startup, so the provider is configured in exactly one place (the API's environment).
- **Provisioning:** first valid SSO login creates the `User` row, keyed on the token's `iss` + `sub` pair. Access is invite-based: a project owner adds members by email with `Read` or `ReadWrite`; a pending-invite row activates when that email first signs in.
- **Bootstrap (first-run setup):** a fresh deployment starts in **setup mode** with a static super admin account whose credentials come from environment config — needed because nothing gates the instance before any SSO user is trusted. A lightweight setup-only auth scheme (alongside JwtBearer) authenticates it. The setup wizard walks through initial configuration (verify SSO sign-in works, set audit retention), and its final step **hands super admin off** to the logged-in SSO user who completed the steps: that `User` gets `IsSuperAdmin = true`, `InstanceSettings.SetupCompleted` flips, and the static account is permanently disabled from then on.
- **Super admin:** the handed-off SSO user. Can create projects and edit instance settings (audit retention); project-level roles govern everything else.
- **Authorization:** resource-based — a single `ProjectAccessHandler` resolving the project from the route and checking membership + role. `Read` can GET and receive SignalR broadcasts; `ReadWrite` required for all mutations. Same check gates hub group joins.

## 4. API surface

REST controllers using DTOs from `Solo.Shared` (never entities). Mutations follow the **save-event** model: one PUT per field-group save, not per keystroke.

```
/api/config/auth                       anonymous: OIDC authority + client ID for the WASM client
/api/setup                             first-run wizard: static super-admin login, instance config, handoff
/api/projects                          CRUD (create: super admin), members (invite/role/remove)
/api/projects/{id}/labels              project label defaults (template for new boards)
/api/boards/{id}/labels                the board's own labels, + reset to the project defaults
/api/projects/{id}/boards              CRUD, open/close
/api/boards/{id}/epics                 CRUD, re-rank
/api/boards/{id}/tasks                 CRUD, re-rank, assign, set status (column move), set/clear epic
/api/tasks/{id}/subtasks               CRUD, re-rank, toggle finished
/api/{epics|tasks}/{id}/comments       add/edit/delete
/api/projects/{id}/audit               paged, filterable query (admin/owner)
```

Validation via FluentValidation (or data annotations — decide at scaffold time); ProblemDetails for errors; `409 Conflict` on concurrency-token mismatch so the client can re-fetch and merge.

**The sidebar's project → board tree needs no new endpoint.** It composes `GET /api/projects` (already returns `ProjectSummaryDto` with the caller's `Role` and `IsOwner`, which is exactly what gates "Add board") with `GET /api/projects/{id}/boards`, fetched lazily the first time a project is expanded. A dedicated `/api/nav` payload was considered and rejected: it would duplicate two endpoints to save requests the user may never make, and it would need its own authorization rules. The N+1 is bounded by how many projects a person actually opens.

## 5. Live editing (SignalR)

Single `BoardHub` with typed client contract in `Solo.Shared`:

- **Groups:** per board (`board:{id}`); membership authorized like the REST API.
- **Change events:** after a successful save, the API layer (via `IHubContext`) broadcasts `EntityChanged(entityType, id, changedFields, actorUserId)` to the board group. Clients patch local state; the sender is excluded. Broadcasting from the API path (not hub methods) keeps REST as the single write path.
- **Presence:** clients report `ViewingEntity(entityType, id)` / `EditingEntity(...)`; the hub tracks connections in an in-memory registry and broadcasts presence so the UI can warn "Hunter is editing this description." In-memory is fine because deployment is single-instance; a Redis backplane is the documented scale-out path if ever needed.

## 6. Audit logging

- **`AuditSaveChangesInterceptor`** on the DbContext: for every modified/deleted entry it walks changed properties and writes one `AuditEntry` per column — timestamp, current user (from a scoped `ICurrentUser` populated by auth middleware), table, row id, column, previous value. Adds cover creation (previous value null). Audit rows are written in the same transaction as the change.
- **Retention:** a `BackgroundService` runs daily and deletes entries older than `InstanceSettings.AuditRetentionDays` (default 90). Retention is the only knob, per concept.
- `AuditEntry` itself is excluded from auditing.

## 7. Persistence & deployment

- Code-first EF Core migrations; applied automatically on startup (acceptable for the single-instance, small-team target).
- **Dockerfile** (multi-stage: publish WASM + API into one image) and **docker-compose.yml** (app + `postgres:17` + volume). All config via environment variables: connection string, OIDC authority + client ID (Google, Microsoft Entra, or any compliant issuer), static setup credentials (used only until the first-run handoff). This is the "easy deployment" story: `docker compose up`.
- CI (GitHub Actions): build, test, publish image.

## 8. Testing strategy

- **Unit:** status rollup (manual status without sub-tasks; derived Unstarted/InProgress/Finished with them), label resolution (board-owned labels, defaults copy/reset), per-provider OIDC claim mapping (Google, Microsoft), audit interceptor output, access-role decisions.
- **Integration:** Testcontainers Postgres + `WebApplicationFactory` with a test auth handler — CRUD round-trips, authz denials (Read user attempting writes), audit rows produced per save event, concurrency 409s, SignalR broadcast received on save, setup handoff (static credentials rejected once `SetupCompleted` is true).

## 9. Build order

| Phase | Deliverable | Exit criterion |
|---|---|---|
| 0 | Solution scaffold, Docker/compose, CI, health endpoint | `docker compose up` serves a hello endpoint |
| 1 | Entities, DbContext, migrations, label seeding | Migrations create full schema; seeds verified by test |
| 2 | OIDC auth (configurable provider), first-run setup wizard + super admin handoff, membership + authz | Sign-in and handoff flows work; static account dead post-setup; role policies enforced in tests |
| 3 | CRUD API for the full hierarchy + comments + labels | Integration suite green over Testcontainers |
| 4 | Audit interceptor + retention background job | Every save event produces correct audit rows |
| 5 | SignalR hub: change broadcast + presence | Two clients see each other's saves and presence live |
| 6 | Hardening: concurrency handling, validation polish, docs | 409 merge path tested; README deployment guide |

Phases 4 and 5 depend only on 1–3 and can proceed in parallel if desired.

## 10. Decisions

All initially open questions were resolved on 2026-07-07:

- **Kanban columns** are three fixed status columns — Unstarted / In Progress / Finished — derived from `Task.Status`. No configurable `Column` entity.
- **Ad-hoc tasks are allowed**: `Task.EpicId` is nullable. Epics render as visual containers surrounding their child tasks; epic-less tasks sit directly on the board. Epics and tasks carry a `Rank` field for user-defined ordering.
- **Epic deletion detaches** its tasks to ad-hoc (`EpicId` → null) rather than deleting them — non-destructive, and the audit log preserves the change.
- **Audit rows store only the previous value.** They are history, not state: the current value is inferable by following the table/row/column link (or the next audit entry for the same column).
- **Comment edits and deletions are audited** like any other save event — the interceptor covers them automatically since comments are ordinary tracked entities.
- **Bootstrap & project creation:** a fresh deployment starts with a static super admin account (env-configured credentials) used for initial configuration. The setup wizard's final step hands super admin status off to the logged-in SSO user who completed it, permanently disabling the static account. That super admin creates projects.
- **Auth provider is generic OIDC**, configured per deployment via authority + client ID environment variables, with Google and Microsoft (Entra ID) as supported presets. The concept's "Google SSO" is the default choice, not a hard dependency; users are keyed on `iss` + `sub`, so nothing else in the system knows which provider is in use.
- **The auth provider is single per deployment** and changing it after setup is unsupported for now (a swap leaves `iss`-keyed accounts unmatched; today the answer is a fresh redeploy). Deliberately not enforced in code, keeping room for a future migration manager that re-links accounts to a new provider.
