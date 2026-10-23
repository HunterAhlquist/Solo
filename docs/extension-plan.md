# Solo — Extension Construction Plan

> **Status (2026-08-18):** implemented on branch `extensions`, phases 1–7. Phase 0 (webhooks + scoped API tokens) was deliberately **not** built — it was always framed as an alternative to this plan rather than a component of it.
>
> Deltas found while building, none of which changed the design:
>
> - **`Solo.Extensibility` cannot be referenced by `Solo.Client`.** It carries a `FrameworkReference` to `Microsoft.AspNetCore.App` and an EF Core dependency, and neither resolves for the `browser-wasm` target. `ContractVersion` and `VersionRange` therefore live in `Solo.Shared/Extensibility/` — dependency-free, referenced by both halves — while the contract package keeps everything a server extension needs. §9's client compat check works against the copy in `Solo.Shared`.
> - **`IDomainEventPublisher` must be scoped, not singleton.** Handlers are scoped (they hold the extension's own `DbContext`), and a singleton resolving them lands on the root provider, which the container refuses outright. Caught by the integration suite on first run.
> - **`SoloPolicies` restated core's policy names wrongly** (`"ProjectRead"` vs core's `"Project:Read"`), which surfaces as a 500 on every extension endpoint that authorizes. `ExtensionPolicyNamesTests` now asserts the two agree — the test §11 called for, written after the drift proved the point.
> - **The project policies need a project in the route.** An extension endpoint with no project/board/task id must use a plain `[Authorize]` and scope its reads through `ISoloQuery`; `SoloPolicies` says so now.
> - **`ExtensionState.Enable`** was added so tests can restore the shared singleton after exercising the disabled gate. Startup never needs it.
>
> Verified: 509 tests green (`Solo.Data.Tests` 51, `Solo.Client.Tests` 255, `Solo.Api.Tests` 203), including a fixture extension loaded from a real package directory into its own `AssemblyLoadContext` against Testcontainers Postgres.

> **Update (2026-08-18):** the plan's worked example — a hypothetical `roadmap` extension — has been replaced in practice by **`solo-mcp`** ([extensions/Mcp/Solo.Mcp](../extensions/Mcp/Solo.Mcp) + [extensions/Mcp/Solo.Mcp.Client](../extensions/Mcp/Solo.Mcp.Client)), which exercises every capability here against a feature people actually want: its own schema and migrations (§6), namespaced endpoints (§5), a client assembly with its own page and static content (§9), and a package built and released by CI.
>
> Building it closed two gaps this plan left open, both now part of the contract:
>
> - **An extension can register middleware and an authentication scheme.** `ISoloExtension.ConfigureServices` runs before `Build()`, so `AddAuthentication().AddScheme<…>()` and `IStartupFilter` both work from the service collection alone — which is how the MCP credential authenticates early enough for core's `CurrentUserMiddleware` to resolve the user. §4 never said so, and the alternative an author would otherwise reach for — post-configuring core's OIDC forward selector by scheme name — couples a package to core's authentication setup.
> - **A client extension can call the API.** §9 delivered routing and nav for extension pages but no way for one to talk to the server: the host registered `HttpClient` only as a typed client for its own API classes. `Solo.Shared.Extensibility.ISoloClientApi` is that seam — implemented by the host's own `ApiClient`, so an extension shares its credential, base address and ProblemDetails handling and cannot end up authenticating differently from the app it runs inside.
>
> Two smaller corrections fell out of the same work. The endpoints serving `GET /api/extensions`, the client assembly and `content/` were made `[AllowAnonymous]`, because the client fetched all three during bootstrap and under the fallback policy the client half of every extension silently never loaded. And a manifest nav entry whose route carries no `{projectAlias}` is now an **instance-level** entry, rendered once in the sidebar footer rather than per project — §9's nav model assumed every extension page belonged to a project.

> **Correction (2026-09-16):** the first of those was the wrong fix for a real symptom, and has been reversed for two of the three endpoints. `GET /api/extensions` and the client assembly are authenticated again; **the load moved instead** — the client now fetches them after sign-in, through the host's authenticated `ApiClient`, and re-renders `App` so the router re-scans routes (§9 below). Nothing needed them anonymous: `ExtensionAccess` is `Read | Write | Owner`, all levels of *project* access, so no manifest can declare a page that renders before sign-in, and the anonymous form published the instance's extension inventory and a set of decompilable assemblies to anyone who asked.
>
> `content/` stays anonymous, and for a structural reason rather than a historical one: a page links it as a browser subresource (`<link rel="stylesheet">`, an `<img>`), which carries no `Authorization` header, and Solo authenticates with a bearer token and no cookie. Same constraint, and the same answer, as a theme's assets. What it exposes is bounded — static files shipped in the package, and the inventory that would name them is not public.

> **Update (2026-10-08): contract 1.8.** Working through a dozen extension ideas beyond Roadmap and MCP turned up the seams they all ran out of, and 1.8 adds them; [writing-extensions.md](writing-extensions.md#contract-18) has the recipe. The decisions worth recording:
>
> - **Background work acts as a user, by grant, and grants live in core.** Outside a request there was no caller, so `ISoloQuery` saw nothing and there was no credential to write with. `ISoloBackground.RunAsAsync` provides both. The rejected alternative was letting an extension act as any user and asking it to store and revoke its own permission, the way `ISoloCredentialIssuer` asks it to revoke tokens. That puts the guarantee that **Sign out everywhere** means everywhere on every extension's diligence. With grants in core (`ExtensionGrants`), core revokes them itself. Grants are made only in the granting user's own request, never from background work, and purging an extension deletes its grants.
> - **Background writes re-enter the pipeline over loopback**, as MCP's do, for the same reason: authorization, other extensions' hooks, audit and broadcast come by construction. Each request carries a credential minted per request, signed with a per-process key and naming the grant, which the handler re-reads every time. Revocation therefore takes effect on the next call, and there is no secret to configure. Background requests never carry super admin (`CurrentUserAccessor.IsSuperAdmin`). Its identity type keeps it outside `CredentialKind`'s allowlists, so it can't mint a session or a hub ticket that would outlive the grant.
> - **User topics are a separate group prefix, not a project topic with a naming convention.** Any member may subscribe to any of a project's topics, so per-person state there would be readable by the whole project. The hub joins `usertopic:{userId}:…` for the connection's own user only.
> - **Task menu items come from classes the host instantiates, not from components.** A client half can't register services, so the host creates contributors with `ActivatorUtilities` from its own provider. Ids are prefixed per contributor, and a slow or failing contributor costs only its own items. A board takes over the right click on cards only while something contributes, and `drag.js` swallows the `contextmenu` Android fires on the same long press that lifts a card.
> - **Card contributions get the board's own state, not a data-loading hook.** A per-board prefetch seam was considered and left out: a static cache keyed by board id, refreshed from a live topic, does the same job inside the extension without widening the contract.

> **Update (2026-10-23): contract 1.10.** Design Docs (live co-edited markdown and sketches) needed a channel for keystroke-rate traffic. [writing-extensions.md](writing-extensions.md#contract-110) has the recipe. The decisions worth recording:
>
> - **Client-sent messages go through core's hub to one handler per extension**, chosen by the topic's prefix and the handler's assembly. The alternative was letting an extension map its own WebSocket endpoint or SignalR hub. Then every live extension would solve sign-in for a socket (a browser can't put the credential in a header, which is why core mints one-use hub tickets), reconnects, the desktop shell's proxy, and per-connection limits again, each a little differently. Through core's hub they get the connection the board already holds, and core keeps the rules: holding the topic is the read check, a separate token bucket per connection, a size cap, a handler timeout.
> - **The sender's role rides on the subscription**, cached when the topic is subscribed and restated by `LiveAccess` when a membership is edited or removed. A database read per keystroke was the alternative; a role read once per connection was the bug it would have had to avoid, since a demoted member would keep writing until they reconnected.
> - **Results, not `HubException`s.** A collaborative editor has to know whether an update landed in order to resend it, and an exception per refused message would be noise in the logs and the client.
> - **Markdown seams are classes the host instantiates**, like task menu contributors, and they fail open: the text renders as written first, so a slow or broken provider costs its labels, never the description.
> - **`blob:` images, not signed image URLs.** Signed URLs would need a signing key and expiry rules in core. A page that already holds the credential can fetch the bytes and mint an object URL itself.

> **Building an extension? Read [writing-extensions.md](writing-extensions.md) instead.** That is the authoring recipe. This document is the design record — the trust model, the decisions and the alternatives rejected — and explains *why* the system is shaped the way it is.

Companion to [backend-plan.md](backend-plan.md) and [frontend-plan.md](frontend-plan.md). Solo already has one working extension subsystem — **themes** ([themes.md](themes.md)) — and this plan deliberately reuses its shape: a zip package, a manifest, a mounted volume, a validating loader, and load failures surfaced in Admin settings instead of taken out on the instance. An operator who can install a theme can install an extension.

Four capabilities are in scope: **new API endpoints**, **new data**, **hooks that intercept existing API calls**, and **new client pages**.

---

## 1. Trust model

Settle this before any code, because everything else follows from it.

A server extension runs in-process with the connection string. A client extension runs in the same JS context as the session token. **There is no sandbox, and in this architecture there cannot practically be one.** Installing an extension is equivalent to deploying code: trust it exactly as much as you trust the image.

This is the same posture `ThemePackageLoader` already documents for itself. The validation in the loader is not a defence against a hostile package — it is what keeps a malformed or truncated zip from taking the instance down, and what turns "the extension didn't work" into a message naming the reason.

The manifest's `permissions` block is **documentation and admin UI, not enforcement**. Building it as though it were a security boundary would be worse than not having it, because it would invite trusting packages that shouldn't be trusted.

The industry split is instructive and lands on our side of the line. Atlassian moved Jira/Confluence away from in-process JVM plugins to out-of-process Forge precisely because in-process plugins made upgrades impossible and one bad plugin could take down a tenant — but that is a hosted multi-tenant product. Home Assistant, Redmine and Discourse all run in-process, fully-trusted, single-operator plugins and it works. Solo is the second category, and in-process is the only route that gets extension pages MudBlazor natively.

## 2. Solution structure

Two new projects:

```
src/
├── Solo.Extensibility   — the public contract: hook interfaces, operation ids, DTO-facing
│                          read API, manifest types. Referenced BY extensions. Semver'd.
└── Solo.Extensions      — the host: package loader, load contexts, hook dispatcher,
                           application-part wiring. Referenced BY Solo.Api only.
```

`Solo.Extensibility` may reference **`Solo.Shared` only**. It must never reference `Solo.Data` or `Solo.Api` — the moment it does, every internal refactor is a breaking change for every installed extension. This accepts that `Solo.Shared` becomes a public contract, which it effectively already is (the desktop MCP tools consume those DTOs).

## 3. Package layout

```
roadmap.zip
├── extension.json          manifest
├── server/                 *.dll — the server assembly and its private dependencies
├── client/                 *.dll — a Razor Class Library and its private dependencies
└── content/                static assets served at /api/extensions/{id}/content/*
```

```jsonc
{
  "id": "roadmap",                    // [a-z0-9-], the route and schema namespace
  "name": "Roadmap",
  "version": "1.2.0",
  "soloApiVersion": ">=1.0 <2.0",     // checked against Solo.Extensibility's version
  "server": "server/Roadmap.dll",
  "client": "client/Roadmap.Client.dll",
  "schema": "ext_roadmap",            // omit for an extension that stores nothing
  "nav": [
    { "route": "/roadmap/{projectAlias}", "label": "Roadmap",
      "icon": "Timeline", "requires": "Read" }
  ],
  "hooks": ["task.update", "task.setStatus"],   // declared up front — see §7
  "failOpen": false
}
```

Installation mirrors themes: drop the zip in the extensions directory (`Extensions__Directory`, `/data/extensions` in the deployment template, a sibling of `/data/themes` created with the same ownership in the Dockerfile).

**Unlike themes, there is no rescan.** Controllers and DI are fixed once `builder.Build()` runs, so installing or updating an extension requires a container restart. Say so plainly in the operator docs — pretending otherwise leads to a design that half-works.

Reuse `ThemePackageLoader`'s hardening wholesale: archive/entry/total size caps, entry-count cap, and normalized-relative-path reads so nothing ever escapes the package or touches disk.

## 4. Loading

Discovery and load happen **first in `Program.cs`**, ahead of the persistence block, because application parts and MVC filters must be registered before `builder.Build()`.

Each extension gets its own `AssemblyLoadContext` with an `AssemblyDependencyResolver`, so extensions can carry their own dependency versions:

```csharp
private sealed class ExtensionLoadContext(string mainPath) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Shared contracts MUST come from Default, or a type crossing the
        // boundary is not the same type on both sides — the classic plugin bug.
        // Returning null here defers to Default rather than loading a private copy.
        if (IsShared(name.Name)) return null;

        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    private static bool IsShared(string? name) =>
        name is not null &&
        (name is "Solo.Extensibility" or "Solo.Shared" ||
         name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ||
         name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal));
}
```

Non-collectible is deliberate: unload would require every EF and MVC reference to drop, which they won't, and restart-to-update is already the contract.

Each server assembly exposes one entry point:

```csharp
public interface ISoloExtension
{
    void ConfigureServices(IServiceCollection services, IExtensionContext context);
}
```

The host then calls `mvcBuilder.AddApplicationPart(assembly)` so the extension's controllers join the pipeline. A version-range mismatch, a missing entry point, or a throwing `ConfigureServices` is recorded as a load failure and surfaced in Admin settings — the instance still starts, exactly as it does for a bad theme.

## 5. API endpoints

Extension controllers are ordinary MVC controllers. Two rules the host enforces rather than trusts:

- **Routes are namespaced.** An `IApplicationModelConvention` applied only to extension application parts rewrites every route to `/api/ext/{extensionId}/…`, regardless of what the extension's `[Route]` says. Without this an extension can shadow `/api/tasks`.
- **Authorization is inherited.** The default and fallback policies in `Program.cs` require a provisioned Solo user, so an extension action shipped without `[Authorize]` fails closed. `Solo.Extensibility` re-exports the `ProjectRead`/`ProjectWrite`/`ProjectOwner` policy names so extensions authorize identically to core.

## 6. Data

### New tables

**Extensions never contribute entity types to `SoloDbContext`.** `Program.cs` runs `MigrateAsync()` against a model code-generated into `SoloDbContextModelSnapshot.cs`; an extension-contributed entity would make the runtime model permanently disagree with that snapshot.

Instead an extension owns its own `DbContext`, in its own Postgres schema, with its own migration history:

```csharp
protected override void OnModelCreating(ModelBuilder b) => b.HasDefaultSchema("ext_roadmap");

// registered by the host from the manifest's "schema":
options.UseNpgsql(conn, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "ext_roadmap"));
```

**There is no second database.** The extension context uses the *same* `ConnectionStrings__Default` as core — same Postgres server, same `solo` database, same `solo-pgdata` volume. A schema is a namespace inside that database, not a separate store. Extension data is therefore backed up by whatever backs up core, and dropped by whatever drops core; nothing new appears in the compose file and no new volume is needed.

### Does it auto-migrate?

Yes. Verified against `postgres:17` with EF Core 10.0.9 / Npgsql 10.0.2, on a database where the schema did not exist:

- The scaffolded migration opens with `migrationBuilder.EnsureSchema(name: "ext_roadmap")`.
- `MigrateAsync()` creates the schema itself. **The host does not need to issue `CREATE SCHEMA` first** — including for the history table, which EF creates before applying any migration.
- Both `Milestones` and `__EFMigrationsHistory` land in `ext_roadmap`; `public` is untouched, so core's own `__EFMigrationsHistory` is never shared or contended.

Extension migrations are scaffolded by the extension author at build time, exactly like core's — they cannot be generated at runtime — so an extension project needs its own `IDesignTimeDbContextFactory`, mirroring `Solo.Data/DesignTimeDbContextFactory.cs`.

### Registration and ordering

An extension registers its context through a host-provided helper rather than plain `AddDbContext`, so the host knows what to migrate and can inject the connection string and history-table configuration itself:

```csharp
public void ConfigureServices(IServiceCollection services, IExtensionContext context) =>
    services.AddExtensionDbContext<RoadmapDbContext>(context);   // records it for migration
```

The host migrates registered extension contexts **after** core, in the same startup block, gated on the **same `Database__AutoMigrate` flag** — an operator who runs core migrations manually in a controlled deploy expects extension migrations to be manual too.

**A failed extension migration must disable that extension, not just be logged.** This is the one ordering trap in the design: services are registered *before* `builder.Build()`, migrations run *after*, so by the time a migration fails the extension's controllers are already routable and its hooks already registered. Without a gate it would serve requests against tables that don't exist. The host therefore keeps a per-extension enabled flag, flipped off on migration failure and consulted by both the hook dispatcher and an action filter on extension routes, which then answer `503` with the reason. The failure is reported in Admin settings alongside load failures, and the rest of the instance runs normally.

### The registry, and uninstall

The manifest names the schema only while the package is present. Delete the zip and restart and `ext_roadmap` is an orphan — rows nobody can attribute to anything. So the host records extensions in **core's own schema**, written on every successful load:

| `ExtensionRegistrations` | |
|---|---|
| `ExtensionId` (PK) | from the manifest |
| `SchemaName` | null for an extension that stores nothing |
| `DisplayName`, `LastSeenVersion` | for the admin list, and for downgrade detection |
| `InstalledAt`, `LastLoadedAt` | `LastLoadedAt` is what identifies an absent package |
| `State` | loaded / failed / absent |

That turns three situations into things Admin settings can show and act on: **loaded** (normal), **failed** (load or migration error — disabled, `503`, reason shown), and **data present, package missing** — the case that is invisible without a registry, and the one that makes this feature worth building.

Uninstall is then two deliberately separate operations:

- **Remove** — delete the package, keep the data. Reversible: drop the same zip back in, restart, migrations no-op, the rows are still there. This is what most operators actually want when they say "uninstall."
- **Purge** — `DROP SCHEMA … CASCADE`, **plus** stripping the extension's key from every core JSONB bag. Irreversible.

Guards on purge, in proportion to it being the most destructive thing the admin UI offers:

- Only available for an extension **not loaded this boot** — package absent, or disabled and then restarted. This sidesteps dropping a schema out from under a live `DbContext` entirely, and matches the restart-based lifecycle §3 already commits to.
- Confirm by typing the extension id.
- The dialog names the `pg_dump -n ext_roadmap …` command rather than offering a download. The runtime image ships no `pg_dump` (it ships no `curl` either — see the compose healthcheck), so export is honestly the operator's step, not something the app pretends to do.
- Writes an explicit audit row. A schema drop is raw DDL that `AuditSaveChangesInterceptor` will never observe, so it has to be recorded deliberately or it happens with no trace.

**The JSONB bag is the leak to remember.** It lives in *core* tables, so dropping the schema doesn't touch it. A purge that does only half the job leaves per-extension keys on every task forever.

There is deliberately **no `IExtensionUninstall` hook on the primary path**: the entire point is that uninstall works when the code is gone. An optional hook can cover state stored outside the schema, but the drop must never depend on the extension being loadable.

One bonus the registry unlocks: with `LastSeenVersion` recorded, the host can refuse to load a **downgrade** — a 1.0 package over a database already migrated by 2.0 — and say so. EF has no notion of a history row being "too new," so without this the extension runs a stale model against a newer schema and fails in confusing ways later.

### Two limits worth stating

**A schema is not a security boundary.** The extension's context holds the core connection string and has full DDL and DML rights over `public` — it can read and rewrite core tables directly with raw SQL. Schema separation prevents *accidents* and keeps migration histories independent; it does not constrain a determined extension. That is §1's trust model, restated where someone might otherwise misread "own schema" as "sandboxed."

**No cross-schema foreign keys into core tables.** An FK from `ext_roadmap` into `public."Tasks"` would block core from ever dropping or renaming that table and would fail core migrations from outside core's control. Extensions reference core rows by bare `Guid`, with no database-level constraint, and tolerate the row having been deleted.

### Reading core data

Extensions do **not** get `SoloDbContext` injected. Two reasons, and the second is the important one:

1. It couples every extension to the entity model — recall the `Task` → `TaskItem` rename, which would have broken every installed extension.
2. It bypasses `ProjectAccessHandler` entirely. An extension querying `db.Tasks` directly sees every project on the instance.

`Solo.Extensibility` exposes `ISoloQuery` instead: scoped to the current caller, returning `Solo.Shared` DTOs, project-filtered before the extension sees a row. Extensions get correct authorization by construction and cannot accidentally build a hole.

### New fields on existing entities

One core migration adds a `jsonb` `Extensions` column to `Task`, `Epic` and `Board`. An extension reads and writes only its own key, through a contract service that enforces the key. One schema change, then never again.

Note the audit consequence: `AuditSaveChangesInterceptor` will record the whole blob as a single column change. Acceptable, but it should be a deliberate choice rather than a discovery.

## 7. Hooks — intercepting existing API calls

The core of the plan, and the part with the most ways to go wrong.

### Not C# `event`

`event Func<TaskUpdatingArgs, Task>` is the obvious-looking shape and the wrong one. Multicast `Invoke()` returns only the **last** handler's `Task`, so awaiting it fire-and-forgets every other subscriber; the first handler to throw aborts the rest of the invocation list; there is no ordering control; handlers get no DI scope, which they need for a scoped `DbContext`; and `-=` across a load context leaks the extension assembly.

Handlers are interfaces resolved from DI, dispatched by host code that controls ordering, timeouts and error policy. Event-shaped registration sugar can be offered in `ConfigureServices` if the ergonomics are wanted, but the dispatch underneath is an explicit ordered list.

### Named operations, not controller signatures

Extensions bind to a versioned operation id, never to `TasksController.Update`. The host owns the mapping, so renaming an action parameter is an internal change instead of a breaking one:

```csharp
[HttpPut("api/tasks/{taskId:guid}")]
[Authorize(Policy = AuthConstants.ProjectWritePolicy)]
[SoloOperation(Operations.TaskUpdate)]     // ← the only line added to core
public async Task<ActionResult<TaskDto>> Update(…)
```

Actions without the attribute are not extensible and pay no cost. Open the Task, Epic and SubTask write paths first; leave everything else closed until there's a reason.

### The contract

```csharp
public interface ISoloOperationHook
{
    IReadOnlySet<string> Operations { get; }   // must match the manifest's "hooks"
    int Order { get; }

    ValueTask<HookOutcome> OnBeforeAsync(OperationContext ctx, CancellationToken ct);
    ValueTask OnAfterAsync(OperationResult result, CancellationToken ct);
}

public abstract record HookOutcome
{
    public sealed record Continue : HookOutcome;
    public sealed record Reject(int Status, string Title, string Detail) : HookOutcome;
}
```

`OperationContext` carries the operation id, the caller (user id, project role, access level), resolved route ids, and typed access to the bound request DTO.

The asymmetry between the two halves is the entire safety story:

- **Before** hooks may veto and may mutate the request. They run in `Order`, each seeing the previous one's mutations.
- **After** hooks may not fail the operation. Wrapped in try/catch, logged, never propagated.

Rejection is a structured `Reject`, not an exception — extensions don't get exceptions as control flow, and the caller gets a real `ProblemDetails` instead of a 500.

### Dispatch

A single `IAsyncActionFilter`, registered globally:

1. Look up the operation id in the current action's endpoint metadata. None ⇒ call `next()` immediately.
2. Resolve hooks declared for that operation, run `OnBeforeAsync` in `Order`.
3. On `Reject`, set `context.Result` to the ProblemDetails and **do not** call `next()`.
4. Otherwise run the action, then dispatch `OnAfterAsync` with the result.

### Failure policy

**A Before hook that throws or exceeds its budget fails the request, and the log names the extension.** Fail closed: it declared intent to veto and then broke, so proceeding would silently ignore a policy the operator installed — the same reasoning as the fallback authorization policy. The budget is ~2s. `failOpen: true` in the manifest opts an advisory hook out, per extension.

`Order` ties break by extension id, so behaviour is deterministic across restarts.

**An admin page listing installed hooks per operation is not optional.** When someone asks why they can't move a card, the answer has to be visible in ten seconds.

### Three caveats that will come up immediately

- **Hooks run post-authorization.** `AuthorizationMiddleware` executes before the MVC filter pipeline, so `ProjectAccessHandler` has already decided by the time a Before hook fires. A hook can further restrict access but can never widen it.
- **Before hooks fire for entities that may not exist.** `LoadTask` runs inside the action, after the filter. A hook on `task.update` fires even when the task is about to 404, and must not assume existence.
- **After hooks are outside the transaction.** The controller has already saved *and* already called `notifier.BoardEntityChanged` by then, so an After hook's failure cannot roll back the core write. "Atomically create a linked row when a task is created" is not achievable this way — that case needs §8's interceptor, or an outbox.

## 8. Events — the notification bus

Distinct from hooks: no veto, total coverage, "tell me what happened."

The seam already exists in embryo. `EntityChangedEvent(EntityType, EntityId, Kind, ChangedFields, ActorUserId)` in `Solo.Shared/Hubs` is a domain event that currently only reaches SignalR. Generalise it: introduce `IDomainEventPublisher`, and make `BoardNotifier` one subscriber alongside extension handlers.

Raise at the existing notifier call sites — they already know board/project scope and changed fields, which a `ChangeTracker`-derived event would not.

Delivery: **after commit, out-of-band, per-handler try/catch with a timeout, failures logged and never propagated.** An extension must not be able to 500 a core write.

For the "must never miss a write, must be atomic" case, the second seam is a `SaveChangesInterceptor` alongside `AuditSaveChangesInterceptor` — inside the transaction, with the full property-level diff, at the cost of no HTTP context and awkward error reporting.

Durable delivery (an outbox table) is **explicitly deferred**. In-process fire-and-forget is right for a single-instance small-team deployment.

## 9. Client pages

`Router` already accepts `AdditionalAssemblies`; `App.razor` currently passes only `AppAssembly`. That plus a service supplying the list is most of the routing work — pages in an extension RCL with `@page` directives are then discovered normally.

Loading: the API serves the client assembly from the package (`GET /api/extensions/{id}/client/{file}`), and the client fetches the bytes and `Assembly.Load(byte[])`s them.

**When** it does that is the one subtlety. Both endpoints are authenticated, and on the web build the credential comes from `GoogleAuthenticationStateProvider` — a service that does not exist until the host is built — so the load cannot happen in `Program.cs` before `builder.Build()`. `ExtensionCatalog` is registered empty; `App.razor` fills it on the first authenticated state it sees and re-renders, which hands `Router` a new `AdditionalAssemblies`, rebuilds the route table and re-resolves the current URL. A deep link to an extension page while signed out therefore has to survive a round trip: signed out, an unmatched route is not "no such page" but "not yet", so it goes to sign-in with the route kept rather than rendering the 404.

**CSP needs no change.** `script-src 'self'` / `connect-src 'self'` already covers assemblies and assets served from our own origin — a happy accident of how `SecurityHeaders.cs` was written.

### Trimming is the blocker

Blazor WASM publishes with `PublishTrimmed=true` in Release. The trimmer removes MudBlazor and framework members *the core app doesn't happen to use*, so an extension calling them throws `MissingMethodException` — in Release only, in production only, with a stack trace pointing nowhere useful.

Two options: set `<PublishTrimmed>false</PublishTrimmed>` on `Solo.Client` (simplest; costs several MB of download, which for a login-gated small-team tool is usually the right trade), or maintain a trimmer root descriptor pinning the guaranteed API surface. **Recommendation: disable trimming, measure the payload, revisit only if it hurts.** The Electron shell serves the same published output and is affected identically.

### Nav and slots

Nav entries come from the manifest, so `NavMenu.razor` renders the sidebar without loading the assembly. Beyond whole pages, a small set of named **slots** is what makes extensions useful for small things: a project-home tab, a section in `DetailDrawer.razor`, a board toolbar button, an Admin settings page. Slots are how an extension adds *a field to the task drawer* rather than only *a whole new page*.

Response enrichment via `IAsyncResultFilter` is **rejected**: it would let any extension rewrite any response body, and the client could no longer trust its own DTOs. The §6 JSONB bag, projected into the DTO by the host, reaches the same outcome with a contract.

### The Electron shell

The shell serves `Solo.Client` from **its own bundle** (`Solo.Desktop/main/server.js` → `serveStatic`) and reverse-proxies only `/api/*`, `/hubs/*` and `/healthz`. That split decides everything about how extension pages behave there.

Works unchanged, no shell code required:

- Extension assemblies and content are fetched from `/api/extensions/…`, which `shouldProxy` forwards to the instance.
- The shell's CSP is a copy of the API's, and the fetch is same-origin against `localhost:41321`, so `connect-src 'self'` covers it — no CSP change on either side.
- `serveStatic` falls back to `index.html` for any non-file path, so an extension route (`/roadmap/ACME`) boots the client and the router resolves it. Deep links work.
- The desktop bundle is produced by the same `dotnet publish Solo.Client`, so the `PublishTrimmed` decision above applies to it automatically.

**What breaks is version skew.** The image and the desktop client version independently (`server-v*` vs `desktop-v*`), so an extension page in the shell composes two separately-updated halves:

| Half | Comes from | Versioned by |
|---|---|---|
| Host client — `Solo.Client`, MudBlazor, `Solo.Shared`, the contract's client half | the shell's own bundle | `desktop-v*`, i.e. whenever the user last updated the app |
| The extension's client assembly | the instance, over the proxy | the extension package on the server |

On web these are always one build. In the shell they are not, and an RCL compiled against a newer contract than the installed shell bundles fails at `Assembly.Load` or first render — on desktop only, only for users who haven't updated. This is the [stale-build gotcha](../src/Solo.Desktop/README.md) generalised, and it is why the client check below is not optional.

- The manifest carries a **separate `soloClientVersion` range** alongside `soloApiVersion`. On web the two are always satisfied together; the shell is the entire reason they are separate fields.
- The client checks that range against its own build version **before** `Assembly.Load`, and on mismatch does not load the assembly, does not register the route, and marks the nav entry unavailable with the reason.
- **Nav entries are gated on the assembly having actually loaded.** A manifest-driven sidebar entry pointing at an unresolvable route renders the router's "That page doesn't exist" — indistinguishable from a bug, and the shell is exactly where it would happen.

**Prerequisite: nothing reports a version today.** `GET /api/config/auth` returns authority, client id, provider and `SetupCompleted`, and nothing else. A compat check needs the API to report its contract version and the client to know its own, so that has to land in the config bootstrap **before phase 6**. It pays for itself independently — it is what lets the shell tell a user their app is older than the instance.

**The alternative, named and not recommended:** proxy the client assets too, serving `Solo.Client` from the instance rather than the bundle. That deletes the skew and makes desktop identical to web, at the cost of pulling the WASM payload over the network on cold start, losing offline start, and undoing the shell's locally-served design. A hybrid — prefer the instance's build, cache it, fall back to bundled — is what to revisit if desktop skew becomes recurring pain. Not worth it for extensions alone.

**Limits.** Extensions cannot reach the shell itself: they cannot add menus, protocol handlers, or any main-process behaviour. To the shell, an extension is only ever more API surface and more Blazor pages. (This paragraph used to carve out `/mcp` as well, on the grounds that the shell handled it locally and never proxied it — which is exactly why the MCP server was moved into an extension and deleted from the shell. An extension *can* add MCP tools now, because the endpoint is the instance's.)

## 10. Compatibility

`Solo.Extensibility` is semver'd independently of the app. The host checks the manifest's `soloApiVersion` range and refuses to load out-of-range packages, reporting them as load failures rather than crashing. Breaking the contract is a major bump and a documented migration note — the whole point of §2's reference restriction is to make those bumps rare.

## 11. Testing

- **Unit:** manifest parsing and range checks; the route-namespacing convention; hook ordering and tie-breaking; Before-hook rejection producing the right ProblemDetails; Before-hook throw/timeout failing closed, and `failOpen` not failing closed; After-hook throw not affecting the response.
- **Integration** (Testcontainers, as core): a fixture extension loaded from a real zip; its endpoint reachable at `/api/ext/…` and unreachable outside it; its migrations landing in their own schema without touching core's history table; `ISoloQuery` returning nothing for a project the caller isn't a member of.
- **Client:** bUnit over a fixture RCL — a route resolving via `AdditionalAssemblies`, a manifest nav entry rendering, a slot filling.

The authorization-bypass test in the integration list is the one that matters most; write it first.

## 12. Decisions

- **In-process, fully trusted.** No sandbox is attempted. The manifest's permissions are documentation. Installing an extension is deploying code, and the docs say so in those words.
- **Extensions never touch `SoloDbContext`'s model.** Own context, own schema, own migration history. This is not negotiable without giving up automatic startup migrations.
- **Extensions never get `SoloDbContext` injected.** `ISoloQuery` over `Solo.Shared` DTOs, pre-scoped to the caller — authorization by construction, and immunity to entity renames.
- **One database, many schemas.** Extensions share `ConnectionStrings__Default` and the existing volume; no second database, no new deployment surface. Backup and restore stay a single operation.
- **Extension migrations obey `Database__AutoMigrate`** and run after core's, each isolated so one failure disables only its own extension.
- **No foreign keys from an extension schema into core tables** — they would hold core's schema hostage. Reference core rows by bare `Guid` and tolerate deletion.
- **The host tracks extensions in a core table**, not from the manifests on disk, so data belonging to a package that is no longer installed stays attributable instead of becoming orphan cruft.
- **Remove and purge are separate.** Removing a package keeps its data and is reversible; purging drops the schema *and* the core JSONB keys, is audited explicitly, and is offered only for an extension not loaded this boot.
- **Hooks bind to named operations, not controller signatures**, so core refactors stay internal.
- **Before hooks fail closed with attribution**, opt-out per extension via `failOpen`. Consistent with the fallback authorization policy.
- **No response rewriting.** Enrichment goes through the JSONB bag.
- **No rescan.** Extension changes require a restart; themes keep their live rescan.
- **Trimming off on `Solo.Client`** rather than a maintained root descriptor, revisited if the payload hurts.
- **Server and client contract ranges are separate manifest fields.** They move together on web and independently in the Electron shell, which bundles its own client build; the client re-checks its own range before loading and degrades to an unavailable nav entry rather than a broken route.
- **Durable event delivery deferred.** In-process fire-and-forget; an outbox if someone ever needs it.
- **`Solo.Shared` becomes a public contract.** Accepted — it effectively already is.


