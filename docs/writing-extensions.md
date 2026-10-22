# Writing a Solo extension

The recipe for building one. For *why* the system is shaped this way — the trust model, the decisions and their alternatives — see [extension-plan.md](extension-plan.md); you do not need it to build an extension.

> **The rule that matters most:** an extension **modifies no core project**. Its server half references the `Solo.Extensibility` contract and nothing else of core; its client half references `Solo.Shared` and MudBlazor. If you are editing `Solo.Api`, `Solo.Data`, `Solo.Client`, `Solo.Shared` or `Solo.Extensions`, you are no longer writing an extension.
>
> The test is removability: uninstall the package and the instance loses that feature and nothing else. The bundled extensions live under `extensions/`, each its own solution outside `Solo.slnx`, so deleting one's folder leaves core building and its tests passing — no core project knows it exists.

**`extensions/Mcp` (`Solo.Mcp` + `Solo.Mcp.Client`) is the worked reference.** A complete extension: own schema and migrations, namespaced endpoints, its own authentication scheme, a client page with static content, packaged by `build/pack-mcp-extension.sh` and released by CI. Read it alongside this guide — its csproj comments explain each constraint where it bites.

An extension can add four things, in any combination:

| Capability | What you write |
|---|---|
| API endpoints | Ordinary MVC controllers, auto-namespaced to `/api/ext/{id}/…` |
| Data | Your own `DbContext` in your own Postgres schema |
| Interception | `ISoloOperationHook` — veto or mutate core write operations |
| Notification | `ISoloEventHandler` — react to committed changes |
| Client pages | A Razor Class Library whose `@page` routes join the app's router |

---

## 1. Create the project

A folder beside the solution, its own repository, or alongside core under `src/` — all work; the loader only ever sees the built package. What matters is the reference list, not the path.

```xml
<!-- Roadmap/Roadmap.csproj — the server half -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- The ONLY Solo reference an extension may take. -->
    <PackageReference Include="HunterAhlquist.Solo.Extensibility" Version="1.7.0" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

The contract ships on nuget.org as two packages: `HunterAhlquist.Solo.Extensibility` for the server half, and `HunterAhlquist.Solo.Shared` for the client half (§9). The package version is the contract version. Compile against the newest one and declare the lowest you actually use in the manifest (§2). [`samples/HelloExtension`](../samples/HelloExtension) is the smallest complete extension built from the packages alone.

The bundled extensions under `extensions/` reference the same packages, but not from nuget.org: `extensions/nuget.config` names what `build/pack-contract.sh` packs into `artifacts/nuget` as their one source, so they build against the contract in this checkout rather than a published one — run the script before building one, and again after changing the contract. Only their test projects reference `src/`, for the host they load the package into.

To start outside this repository without typing any of this, `dotnet new install HunterAhlquist.Solo.Templates` adds a **Solo Extension** template to `dotnet new`, Visual Studio and Rider. It generates both projects, the manifest and a `pack.proj` that builds the package, with options for a schema of its own, a hook and an event handler. Its source is [`templates/`](../templates); `build/pack-templates.sh` packs it and builds an extension from each combination of options.

If you need something the contract doesn't offer, that's a finding about the contract — raise it rather than reaching into `Solo.Data` or `Solo.Api`.

## 2. Write the manifest

`extension.json` at the package root:

```jsonc
{
  "id": "roadmap",                  // [a-z0-9-]; the route segment and schema namespace
  "name": "Roadmap",
  "version": "1.0.0",
  "soloApiVersion": ">=1.0 <2.0",   // required when you ship a server assembly
  "soloClientVersion": ">=1.0 <2.0",// required when you ship a client assembly
  "server": "server/Roadmap.dll",
  "client": "client/Roadmap.Client.dll",
  "schema": "ext_roadmap",          // MUST be "ext_" + id, hyphens → underscores
  "nav": [
    { "route": "/roadmap/{projectAlias}", "label": "Roadmap",
      "icon": "Timeline", "requires": "Read" }
  ],
  "hooks": ["task.setStatus"],      // operations your hooks may run on
  "failOpen": false
}
```

**`schema` is derived, not chosen.** The loader computes `ext_` + your id (hyphens become underscores) and rejects a manifest that says anything else — so a purge can never be pointed at core's data or another extension's.

**`hooks` is a declaration, not a suggestion.** A hook registered for an operation the manifest doesn't list is dropped at dispatch, so the admin page's "which extensions sit on this write path" stays honest.

## 3. The entry point

Exactly one `ISoloExtension` per server assembly. The host finds it by reflection and calls it once, before the container is built.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Roadmap;

public sealed class RoadmapExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        services.AddExtensionDbContext<RoadmapDbContext>(context);
        services.AddOperationHook<MilestoneFreezeHook>();
        services.AddEventHandler<TaskRollupHandler>();
        services.AddScoped<MilestoneService>();   // your own services, as normal
    }
}
```

Needs a public parameterless constructor. Throwing here is recorded as a load failure and disables the extension — the instance still starts.

## 4. Data

Your own context, your own schema. **Never add entity types to `SoloDbContext`.**

```csharp
public sealed class Milestone
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public Guid? TaskId { get; set; }   // bare Guid — no FK into core (see below)
}

public sealed class RoadmapDbContext(DbContextOptions<RoadmapDbContext> options) : DbContext(options)
{
    public DbSet<Milestone> Milestones => Set<Milestone>();

    protected override void OnModelCreating(ModelBuilder b) => b.HasDefaultSchema("ext_roadmap");
}
```

`AddExtensionDbContext<T>()` supplies the connection string and pins the migrations history table into your schema — don't call `AddDbContext` yourself.

Migrations are scaffolded at build time exactly like core's, so you need a design-time factory:

```csharp
public sealed class RoadmapDesignTimeFactory : IDesignTimeDbContextFactory<RoadmapDbContext>
{
    public RoadmapDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RoadmapDbContext>()
            .UseNpgsql("Host=localhost;Database=solo;Username=solo;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "ext_roadmap"))
            .Options);
}
```

```bash
dotnet ef migrations add InitialRoadmap --project Roadmap --startup-project Roadmap
```

The host runs your migrations at startup after core's, gated on the same `Database__AutoMigrate`. A failure disables only your extension — its endpoints answer `503` naming the reason.

**No foreign keys into core tables.** An FK from `ext_roadmap` into `public."Tasks"` would stop core from ever dropping or renaming that table. Reference core rows by bare `Guid` and tolerate the row having been deleted.

**To add a field to an existing core entity**, don't add a table — use the per-entity JSON bag, which is scoped so you can only touch your own key:

```csharp
await fields.SetAsync(ExtensionEntity.Task, taskId, new { milestoneId = id });
var mine = await fields.GetAsync<MyFields>(ExtensionEntity.Task, taskId);
```

## 5. Reading core data

Extensions never get `SoloDbContext`. `ISoloQuery` is pre-scoped to the calling user, so project authorization holds by construction:

```csharp
Task<IReadOnlyList<ProjectSummaryDto>> ProjectsAsync(CancellationToken ct = default);
Task<IReadOnlyList<BoardDto>>          BoardsAsync(Guid projectId, CancellationToken ct = default);
Task<IReadOnlyList<EpicDto>>           EpicsAsync(Guid boardId, CancellationToken ct = default);
Task<IReadOnlyList<TaskDto>>           TasksAsync(Guid boardId, CancellationToken ct = default);
Task<TaskDto?>                         TaskAsync(Guid taskId, CancellationToken ct = default);
Task<bool>                             CanWriteBoardAsync(Guid boardId, CancellationToken ct = default);
```

A caller who isn't a member of a project gets an empty list, not an error — mirroring core's own endpoints.

## 6. Endpoints

Ordinary controllers. The host rewrites every route to `/api/ext/{id}/…` regardless of what your `[Route]` says, so you cannot shadow a core endpoint.

```csharp
[ApiController]
[Route("milestones")]
public sealed class MilestonesController(RoadmapDbContext db, ISoloQuery query) : ControllerBase
{
    // Reachable at /api/ext/roadmap/milestones/{boardId}
    [HttpGet("{boardId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<IReadOnlyList<string>> List(Guid boardId, CancellationToken ct) =>
        await db.Milestones.Select(m => m.Name).ToListAsync(ct);
}
```

**Choosing the right `[Authorize]`:**

- The route carries a `projectId`/`boardId`/`epicId`/`taskId`/`subTaskId` → use `SoloPolicies.ProjectRead` / `ProjectWrite` / `ProjectOwner`. They resolve the project from those route values.
- The route carries none of them → a project policy **cannot resolve a project and will deny**. Use a plain `[Authorize]` and scope your reads through `ISoloQuery`.
- A plain `[Authorize]` — and omitting it entirely, since the fallback policy is fail-closed — requires a **provisioned Solo user**, not just a valid sign-in. Anyone with a Google account can get an ID token for the instance from its sign-in page; an identity nobody invited is refused before your action runs.

## 7. Hooks — intercepting core writes

Bind to a named operation, never to a controller signature. The full list is on `Operations`.

```csharp
public sealed class MilestoneFreezeHook(RoadmapDbContext db) : ISoloOperationHook
{
    public IReadOnlySet<string> Operations { get; } =
        new HashSet<string> { Extensibility.Operations.TaskSetStatus };

    public int Order => 10;   // ascending; ties break by extension id

    public async ValueTask<HookOutcome> OnBeforeAsync(OperationContext context, CancellationToken ct)
    {
        // The entity may not exist — resource lookup happens inside the action,
        // after this runs. Never assume it does.
        if (context.Target.TaskId is not { } taskId) return HookOutcome.Proceed;

        return await db.Milestones.AnyAsync(m => m.TaskId == taskId && m.IsFrozen, ct)
            ? HookOutcome.Reject(409, "Milestone frozen", "This task's milestone is frozen.")
            : HookOutcome.Proceed;
    }
}
```

Rules worth internalizing:

- **Before may veto and may mutate** `context.Arguments`; later hooks and the action see the change.
- **After may not fail the operation** — the write has already committed and already broadcast over SignalR. If you need atomicity, you need a `SaveChangesInterceptor`, not an After hook.
- **A Before hook that throws or exceeds ~2s fails the request closed**, and the log and the `ProblemDetails` name your extension. Set `"failOpen": true` in the manifest only for genuinely advisory hooks.
- **Hooks run after authorization** — you can further restrict access, never widen it.

## 8. Events — reacting to committed changes

No veto, and a throw can't break the core write.

```csharp
public sealed class TaskRollupHandler(RoadmapDbContext db) : ISoloEventHandler
{
    public IReadOnlySet<string> EntityTypes { get; } = new HashSet<string> { "task" };

    public async ValueTask HandleAsync(SoloDomainEvent domainEvent, CancellationToken ct)
    {
        // domainEvent: EntityType, EntityId, Kind, ChangedFields, ActorUserId, ProjectId, BoardId
    }
}
```

Delivery is in-process and best-effort — there is no outbox, so don't build anything that must never miss an event.

## 9. Client pages

A Razor Class Library. Its `@page` routes join the app's router automatically, and MudBlazor, dialogs and JS interop all resolve from the host's service provider.

**It cannot reference `Solo.Extensibility`** — that carries a FrameworkReference to ASP.NET Core plus EF Core, and neither resolves for `browser-wasm`. Reference `Solo.Shared` (the `HunterAhlquist.Solo.Shared` package) instead, take `Components.Web` and MudBlazor as packages at the versions the instance runs, and turn off static web assets (the package's own `content/` folder carries CSS):

```xml
<!-- Roadmap.Client/Roadmap.Client.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>
  </PropertyGroup>

  <ItemGroup>
    <SupportedPlatform Include="browser" />
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.12" />
    <PackageReference Include="MudBlazor" Version="9.11.0" />
    <PackageReference Include="HunterAhlquist.Solo.Shared" Version="1.7.0" />
  </ItemGroup>
</Project>
```

**To call the API, inject `ISoloClientApi`** (`Solo.Shared.Extensibility`). The host registers `HttpClient` only as a typed client for its own API classes, so this is the seam — and because the implementation *is* the host's client, the extension shares its credential, base address and ProblemDetails handling. It cannot end up authenticating differently from the app it runs inside.

```razor
@page "/roadmap/{ProjectAlias}"
@inject ISoloClientApi Api

<MudText Typo="Typo.h1">Roadmap</MudText>

@code {
    [Parameter] public string ProjectAlias { get; set; } = "";

    protected override async Task OnInitializedAsync()
    {
        var milestones = await Api.GetAsync<List<string>>($"api/ext/roadmap/milestones/{BoardId}");
        // Non-success throws SoloApiException carrying the ProblemDetails.
    }
}
```

**`Resolve` vs `ResolveExternal` is a real trap.** Both build absolute URLs; use `Resolve` for something *this browser* fetches (a stylesheet from `content/`, an image) and `ResolveExternal` for any address a **person copies out of the app** into another tool. They differ in exactly one place that matters — the desktop shell, where `Resolve` yields `http://localhost:41321/…`, correct for a fetch and useless pasted into someone else's config.

**Version your `content/` URLs** (`?v=` plus your package version) when the files change with your client code, as a script your pages call into does. The host sends them without cache headers, but a proxy in front of it may add its own, and the desktop app's cache survives a restart. An unversioned URL can then pair a new page with an old script. `Solo.Roadmap.Client`'s `RoadmapApi.Content` does this.

**Don't put a stylesheet in `<HeadContent>`.** Only one `HeadContent` renders at a time, and the host's theme provider already uses it for the theme's font and stylesheet links — a page's own replaces them, and the instance theme vanishes from the whole app while that page is open. Render the `<link rel="stylesheet">` directly in the page markup instead (`Solo.Mcp.Client`'s `McpAccess.razor`), or append it once from a JS module (`ensureStyles` in `Solo.Roadmap`'s `content/roadmap.js`). `<PageTitle>` is fine; it is a separate outlet.

**`content/` is served anonymously; the assembly is not.** A `<link>` or `<img>` carries no `Authorization` header, so the host has no way to authenticate a subresource fetch — put nothing in `content/` you would not hand to a stranger, since it is a static file in a package anyone holding the package already has. The inventory and the client assembly are authenticated, so neither is reachable without a signed-in user.

**There is no pre-authentication extension page.** The client half is fetched after sign-in, so its routes do not exist before then — and `requires` can only name a level of *project* access anyway. A page that needs to render on the sign-in screen is a theme, not an extension.

**Nav entries** come from the manifest and only render if the assembly actually loaded, so a version mismatch degrades to a missing entry rather than a dead link. A route containing `{projectAlias}` renders **per project** inside that project's group; a route without one is an **instance-level** entry, rendered once in the sidebar footer.

Declare `soloClientVersion` honestly: the client re-checks it against its own build before loading, because the Electron shell bundles a client that can be older than the instance.

## 10. Advanced: middleware and authentication schemes

`ConfigureServices` runs before `Build()`, so the service collection is enough to register both — an extension that needs its own credential format doesn't have to touch core's authentication setup:

```csharp
services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MyHandler>("my-scheme", null);
services.AddSingleton<IStartupFilter, MyMiddlewareFilter>();
```

`extensions/Mcp/Solo.Mcp/Auth/` does exactly this so an MCP credential authenticates early enough for core's `CurrentUserMiddleware` to resolve the user from it. Prefer this over post-configuring core's OIDC forward selector by scheme name, which couples the package to core's setup.

## 11. Package and install

```
roadmap.zip
├── extension.json
├── server/Roadmap.dll        + private dependencies
├── client/Roadmap.Client.dll + private dependencies
└── content/                  optional static assets
```

**"Private dependencies" means yours alone.** The instance supplies `Solo.Shared`, `Solo.Extensibility`, MudBlazor, ASP.NET Core, EF Core and Npgsql, and a package's own copy of any of them is never the one that runs. A build's output folder contains all of them, so copy out your own assemblies rather than zipping the folder — `build/pack-mcp-extension.sh` filters the same list.

Drop the zip in the extensions directory (`Extensions__Directory`, `/data/extensions` in the deployment template) and **restart** — controllers and DI are fixed once the host is built, so there is no rescan.

**While developing**, skip the zip: point `Extensions__DevelopmentDirectories__0` at a package-shaped directory and it loads the same way through the same validation.

```bash
dotnet build Roadmap -o ./pkg/server
cp Roadmap/extension.json ./pkg/
Extensions__DevelopmentDirectories__0=$PWD/pkg dotnet run --project src/Solo.Api
```

## 12. Verify it loaded

Admin settings lists every extension with its state, and any package that failed to load with the reason. States: **loaded**, **disabled** (load or migration failure — endpoints answer `503`), **data present, package missing** (the zip is gone but its schema survives).

Then check the obvious things:

```bash
curl -H "Authorization: Bearer $SOLO_TOKEN" \
  https://your-instance/api/ext/roadmap/milestones/$BOARD_ID
```

```sql
\dn                         -- ext_roadmap exists
\dt ext_roadmap.*           -- your tables + your own __EFMigrationsHistory
```

**Read `extensions/Mcp` for a real one.** It is a complete extension — schema, migrations, endpoints, its own auth scheme, a client page, static content, CI packaging — and its csproj comments explain each constraint where it applies.

`tests/Solo.Extension.Fixture/` is the other working package, but it is a *test* fixture: a server half with a one-page client half beside it (`tests/Solo.Extension.Fixture.Client/`), and its hook deliberately misbehaves on an `X-Fixture-Hook` header to exercise reject/throw/timeout paths. Copy its structure if you like; don't copy that behavior.

---

## Common mistakes

| Symptom | Cause |
|---|---|
| Core migrations start failing, or the model won't match the snapshot | Entity types were added to `SoloDbContext`. Extensions own a separate context. |
| The extension sees projects the caller isn't a member of | `SoloDbContext` was injected instead of `ISoloQuery`. |
| Every endpoint returns 500 | A project policy on a route with no project/board/task id — it can't resolve a project. Use plain `[Authorize]`. |
| Package rejected at load with a schema message | `schema` isn't `ext_` + the id with hyphens as underscores. |
| The hook never fires | The operation isn't in the manifest's `hooks` list, or the core action carries no `[SoloOperation]`. |
| Changes don't take effect | No rescan exists — restart the instance. |
| Endpoints answer `503` | The extension is disabled; Admin settings names the reason (usually a failed migration). |
| Nav entry missing on desktop but fine in the browser | `soloClientVersion` excludes the shell's bundled client build. |
| Client project won't restore for `browser-wasm` | It references `Solo.Extensibility`. Reference `Solo.Shared` + `Components.Web` + MudBlazor instead. |
| No way to call the API from a client page | Inject `ISoloClientApi` — the host registers `HttpClient` only for its own typed clients. |
| A copied URL sends people to `localhost:41321` | `Resolve` was used where `ResolveExternal` was needed. |
| Nav entry renders once instead of per project | Its route has no `{projectAlias}`, which makes it instance-level by design. |
| The theme's styling disappears while one page is open | That page uses `<HeadContent>`, which replaces the theme provider's. Put the `<link>` in the page body instead. |

## Task-detail contributions and audit sources (contract 1.2)

A client component marked `[TaskDetailExtension]` (from
`Solo.Shared.Extensibility`) is rendered below task assignments. It receives
`Task` (`TaskDto`), `CanWrite` (`bool`), and `OnChanged` (`EventCallback`). Invoke
`OnChanged` after saving so the host refreshes the task's concurrency version.
Each contribution has an error boundary; only loaded extension assemblies can
contribute. Eligibility and authorization still belong in your extension API.
`Solo.Mcp.Client/LlmWork.razor` is the example.

Use the keyed `IExtensionFieldStore` with `[FromKeyedServices("your-id")]`.
`SetVersionedAsync` takes the core entity version and throws
`DbUpdateConcurrencyException` on conflict; translate it to HTTP 409. Field
writes are audited and broadcast to connected board clients after commit.
`ISoloQuery.TaskAssigneeIdentitiesAsync` returns the OIDC identities of assignees
only when the caller can read the task. Use it server-side for integrations;
do not return identity subjects or credential records to the client.

Register an `ISoloAuditContributor` to annotate core audit entries. `ShouldRecord`
selects requests that need attribution. The host calls `RecordAsync` with the new
audit ids and a shared connection/transaction before committing. Write only to
your extension's own schema; do not commit or dispose the supplied transaction.
A write failure rolls back the host-owned save. With an existing explicit
transaction, its caller remains responsible for rollback. No core entity or
DbContext crosses this contract, and no core migration is needed.

`ReadAsync` receives only the ids on an already-authorized audit page. Its labels
are returned in `AuditEntryDto.Annotations`, keyed by the host using the
extension's id, and rendered by the audit UI. Read failures are logged and do
not hide the core audit page. Disabled or uninstalled contributors do not run.
The MCP extension's `McpAuditContributor` and `ext_mcp.AuditAttributions` show the
pattern; removing MCP removes these annotations from the UI while preserving
core audit history.

## Contract 1.3

Declare `"soloApiVersion": ">=1.3 <2.0"` (and the same `soloClientVersion` for a
client half) to use anything in this section. `extensions/Roadmap`'s
`Solo.Roadmap` and `Solo.Roadmap.Client` use every addition here (calling `task.setSchedule`
rather than hooking it), if you want a working example.

### Task start dates and `task.setSchedule`

`TaskDto.StartAt` (`DateOnly?`, the record's last parameter) is a task's optional
first day. With it, `DeadlineAt` is the end of a date range rather than only a due
date. A task's **effective start** is `StartAt ?? DateOnly.FromDateTime(CreatedAt)`,
so a task with only a deadline still has a range.

A start needs a deadline and can't fall after it. `PUT api/tasks/{taskId}/schedule`
with `{ startAt, deadlineAt, version }` is the only way to set it, and it answers
400 for either violation and 409 for a stale version. `PUT api/tasks/{taskId}` never
carries a start, so older clients can't wipe one. When that update clears the
deadline, or moves it before the start, the server clears the start.

The schedule endpoint is hookable as `Operations.TaskSetSchedule`
(`"task.setSchedule"`). `ISoloQuery` stays read-only: an extension that changes
a task's dates does it from its client half, through `ISoloClientApi`, so the
change passes through core's authorization, hooks, audit and broadcast like
any other edit.

### Project sidebar sections

A project's group in the sidebar is split into labelled sections: **Boards**
first, then one per contributed section, then Project settings and manifest
nav entries below a plain divider. A client component marked
`[ProjectNavSection("Title", Order = 0)]` (from `Solo.Shared.Extensibility`)
becomes one of those sections. Core draws the `Title ────` header, and your
component renders only the items under it, usually `MudNavLink`s plus the
section's own create action. It receives:

| Parameter | Type | |
|---|---|---|
| `Project` | `ProjectSummaryDto` | The project whose group this is. |
| `CanWrite` | `bool` | The same test core uses to show **Add board**. Use it to gate your create action. |

Sections are ordered by `Order`, then by extension id. Each renders inside its
own error boundary: a section that throws shows one line of error text and the
rest of the sidebar keeps working. Only loaded extension assemblies contribute,
and they load after sign-in, so a section appears a moment after the boards do.
A project group renders its children only once it is expanded. Load your data
when the component first renders, not before.

A client half can't register DI services, because its assembly loads after the
container is built. To refresh the section when your own pages change something,
use a static event inside your assembly and unsubscribe in `Dispose`.

### Opening the task drawer: `ISoloTaskDrawer`

To show a task in the same drawer a board uses, with its presence, editing
warning, checklist and comments, inject `ISoloTaskDrawer` (from
`Solo.Shared.Extensibility`) into a client page and call
`OpenAsync(taskId)`. The drawer opens over your page. You can't render
`DetailDrawer` yourself, because it depends on board state inside `Solo.Client`.

```razor
@inject ISoloTaskDrawer Drawer
@implements IDisposable

@code {
    protected override void OnInitialized() => Drawer.Changed += OnDrawerChanged;

    private void OnDrawerChanged(TaskDrawerChange change) => InvokeAsync(() =>
    {
        // Saved: change.Task is the fresh TaskDto, version included.
        // Moved: change.Task has its new board; the drawer then navigates there.
        // Deleted: change.Task is null.
        StateHasChanged();
    });

    public void Dispose() => Drawer.Changed -= OnDrawerChanged;
}
```

`Changed` reports every change to the open task: edits in the drawer, checklist
ticks, assignee changes, and remote edits. `Closed` fires when the drawer closes.
The drawer uses the board page's permission rules, so a read-only member sees
it read-only. Navigating away closes it. If a board page is already showing a
different board when you call `OpenAsync`, the host follows the task to its own
board page, as a task link does.

### Settings panels on Admin settings

Admin settings lists every extension. A client component marked
`[ExtensionSettings]` (from `Solo.Shared.Extensibility`) renders inside its own
extension's entry there, below the id, version and schema line. It receives one
parameter, `ExtensionId` (`string`).

The panel only renders for a **loaded** extension whose client assembly loaded.
A disabled extension, or one whose package is missing, shows no panel. Each
panel has its own error boundary, so one that throws shows a line of error text
and the rest of the page keeps working.

Nothing about this is special on the server. Your panel calls your own
endpoints through `ISoloClientApi`, and those endpoints guard themselves with
`[Authorize(Policy = SoloPolicies.SuperAdmin)]`. The page is for super admins,
but that alone doesn't protect an endpoint.

### Who is calling: `ISoloQuery.CallerAsync`

`CallerAsync()` returns the signed-in caller as an `ExtensionCaller`, with their
Solo user id, email, display name and super-admin flag, plus their role and owner
flag when the route names a project. Use it to record who created a row in your
own schema. The user id is `Guid.Empty` for an anonymous request.

## Contract 1.4

Declare `"soloApiVersion": ">=1.4 <2.0"` (and the same `soloClientVersion` for a
client half) to use anything in this section. Roadmaps 1.2 uses all of it.

### Project home and project card sections

Two more places a client component can contribute to, both from
`Solo.Shared.Extensibility` and both given the same parameters as a sidebar
section (`Project` and `CanWrite`):

| Attribute | Where it renders |
|---|---|
| `[ProjectHomeSection("Title", Order = 0)]` | The project home's first tab, below the boards. Core puts a **Boards** header over the boards and a `Title` header over each section. The tab is named after them ("Boards & Roadmaps"). |
| `[ProjectCardSection("Title", Order = 0)]` | Each project's card on All projects, as a surface of its own below the card's boards, titled `Title`. |

Core draws the header or title. Your component renders only what goes under it.
As with sidebar sections, each one has its own error boundary, sections order by
`Order` and then extension id, and they appear a moment after the boards do,
once extension assemblies have loaded.

A card section renders once per project on All projects, so it loads once per
project. Keep its read cheap.

### Live topics: `ISoloLiveUpdates` and `ISoloLiveTopics`

Core's own broadcasts cover core's entities. To keep your pages current while
someone else edits your data, publish on a **topic** from the server half and
subscribe from the client half. Both go over the host's existing realtime
connection.

```csharp
// Server half: after the change is saved.
public sealed class PlansController(PlansDbContext db, ISoloLiveUpdates live) : ControllerBase
{
    [HttpPut("projects/{projectId:guid}/plans/{id:guid}")]
    public async Task<IActionResult> Update(Guid projectId, Guid id, UpdatePlanRequest request, CancellationToken ct)
    {
        // ... save ...
        await live.PublishAsync(projectId, $"plans:{id:N}", "changed", ct: ct);
        return NoContent();
    }
}
```

```csharp
// Client half.
[Inject] private ISoloLiveTopics Live { get; set; } = null!;

private IAsyncDisposable? _live;

protected override async Task OnInitializedAsync()
{
    _live = await Live.SubscribeAsync(Project.Id, $"plans:{PlanId:N}",
        message => InvokeAsync(ReloadAsync),
        others => InvokeAsync(() => { _others = others; StateHasChanged(); }));
    await Live.SetPresenceAsync(Project.Id, $"plans:{PlanId:N}", PresenceMode.Viewing);
}

public async ValueTask DisposeAsync()
{
    if (_live is not null)
    {
        await _live.DisposeAsync();
    }
}
```

- **A message is a nudge, not state.** `Kind` and `Data` are yours to define. Say
  what changed and refetch through your API, which applies the access checks.
  Publishing doesn't check access; only members of the project can subscribe to
  its topics.
- **The sender is left out.** When the publishing request came from a client
  with a live connection, that connection doesn't hear its own change, as with
  core's broadcasts. Its page already applied it.
- **Topics** are up to 200 characters and are scoped to the project. Prefix them
  with something of your own so two extensions can't collide. A connection can
  hold 64 at once.
- **Presence** is optional and separate from subscribing. `SetPresenceAsync`
  reports the signed-in user as `Viewing` or `Editing` on a topic they hold, and
  `PresenceMode.None` clears it. The `onPresence` callback hears everyone else on
  the topic, one entry per user (their strongest mode), with the signed-in user
  left out. Disposing the subscription clears presence.
- **Reconnects are handled.** Held topics and reported presence are restated on
  the new connection. Delivery is best-effort, so a page should still work if it
  hears nothing.
- **Callbacks arrive off the render cycle**, so wrap them in `InvokeAsync`. A
  handler that throws is logged and doesn't stop other subscribers.

## Contract 1.5

Declare `"soloClientVersion": ">=1.5 <2.0"` to use this section. It adds nothing
on the server, so `soloApiVersion` can stay where it was. Roadmaps 1.3 uses it.

### Choosing a task: `ISoloTaskPicker`

To let someone choose one of a project's tasks, inject `ISoloTaskPicker` (from
`Solo.Shared.Extensibility`) into a client page and call `PickAsync`. Don't
build your own picker. The host's picker searches by name, serial or epic, and
groups by board and then by epic in the order the board draws them, epic groups
included. Finished tasks and closed boards' tasks are hidden until the person
switches them on.

```csharp
[Inject] private ISoloTaskPicker Picker { get; set; } = null!;

private async Task LinkAsync()
{
    var task = await Picker.PickAsync(Project.Id, new TaskPickerOptions
    {
        Title = "Link a task",
        Exclude = _linked.Select(l => l.TaskId).ToHashSet(),
        AllExcludedText = "Every task in this project is already linked.",
    });
    if (task is null)
    {
        return; // Cancelled.
    }

    await Client.SendAsync(HttpMethod.Post, $"api/ext/links/projects/{Project.Id}/links",
        new { taskId = task.Id });
}
```

- **It returns a `TaskDto`, or null** when the person cancels or your
  `CancellationToken` fires, which also closes the dialog. Navigating away
  closes it too.
- **`Exclude` is yours to fill.** Use it for what your feature already has,
  such as the tasks on this roadmap. The picker reads core's API as the
  signed-in user, so it can't know your data, and it only offers tasks that
  user can already see.
- **Picking isn't permission.** Your own endpoint still checks that the task
  belongs to the project and that the caller can write.
- **Presence is yours.** If your page reports `Editing` while one of its own
  dialogs is open, do the same around `PickAsync`.

## Contract 1.6

Declare `"soloClientVersion": ">=1.6 <2.0"` to use this section. Like 1.5 it
adds nothing on the server. Roadmaps 1.4 uses it.

### Right-click menus: `ISoloContextMenu`

To show a menu where someone right-clicked, inject `ISoloContextMenu` (from
`Solo.Shared.Extensibility`) and call `ShowAsync` with the pointer event's
viewport position and the items. Don't draw your own. In the desktop app the
host shows the operating system's own menu, which a page can't reach any other
way; in a browser it draws one that matches the app.

```csharp
[Inject] private ISoloContextMenu Menu { get; set; } = null!;

private async Task OnRowMenuAsync(MouseEventArgs e, Milestone milestone)
{
    var choice = await Menu.ShowAsync(e.ClientX, e.ClientY,
    [
        new ContextMenuItem("open", "Open milestone") { Icon = Icons.Material.Filled.OpenInNew },
        new ContextMenuItem("delete", "Delete milestone")
        {
            Icon = Icons.Material.Filled.Delete,
            Danger = true,
            SeparatorBefore = true,
        },
    ]);

    switch (choice)
    {
        case "open":
            await OpenAsync(milestone);
            break;
        case "delete":
            await DeleteAsync(milestone);
            break;
    }
}
```

- **It returns the chosen item's `Id`, or null** when the person dismisses the
  menu, when your `CancellationToken` fires, or when another menu opens over
  it. Navigating away dismisses it too.
- **Suppress the browser's own menu.** Handle `contextmenu` with
  `@oncontextmenu:preventDefault="true"`, or call `preventDefault()` in your
  own script before calling into .NET. Otherwise the browser's menu opens as
  well.
- **The label is all a native menu shows.** `Icon` and `Danger` only change the
  drawn menu, so a destructive item has to say so in its words.
- **Leave out what the person can't do.** The menu doesn't check permissions;
  your endpoint does. An item that would only fail is worse than no item.
- **Touchscreens have no right-click.** Open the same menu from a long press, or
  from a visible button, such as a `⋯` on the row.

## Contract 1.7

Declare `"soloApiVersion": ">=1.7 <2.0"` to use this section. It adds nothing
on the client. MCP 1.6 uses it.

### Credentials you issue: `ISoloCredentialIssuer`

If your extension mints credentials of its own on a user's behalf (API
tokens, keys), register a revoker for them. A super admin's **Sign out
everywhere** in Admin settings revokes core's sessions and then calls every
enabled issuer, so one action cuts off everything that person holds. Without
one, your tokens survive it.

```csharp
public sealed class TokenIssuer(MyDbContext db) : ISoloCredentialIssuer
{
    public Task<int> RevokeAllAsync(CredentialHolder holder, CancellationToken ct) =>
        db.Tokens
            .Where(t => t.Issuer == holder.Issuer && t.Subject == holder.Subject && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
}

// In ConfigureServices:
services.AddCredentialIssuer<TokenIssuer>();
```

- **Return how many you revoked.** The count is shown to the admin.
- **It's awaited, and a failure is reported.** Unlike an event handler, this
  runs in the admin's request. If it throws, the admin is told your extension
  couldn't revoke its credentials. Core's sessions stay revoked either way.
- **Match on issuer and subject**, the pair core keys users on, rather than on a
  stored email. An email can change; the identity can't.
- **Put only `iss` and `sub` on a principal your credential produces.** Core's
  provisioning reads an `email` claim as the provider's current word for it. It
  activates pending invites to that address and overwrites the user's own. A
  copy you stored when the token was minted is neither current nor verified.

## Contract 1.8

Both halves changed. Declare `"soloApiVersion": ">=1.8 <2.0"` to use the
server additions and `"soloClientVersion": ">=1.8 <2.0"` to use the client ones;
a package using only one half's additions needs 1.8 only in that range.

### More hookable operations

Three more write paths carry `[SoloOperation]`:

| Operation | Endpoint | Note |
|---|---|---|
| `task.setBoard` | `PUT api/tasks/{taskId}/board` | Moving to another board. Not `task.move`, which reorders within one. The destination is the request's `BoardId`; `Target.BoardId` is empty. |
| `task.setAssignees` | `PUT api/tasks/{taskId}/assignees` | The request carries the whole new list. |
| `epic.setAssignees` | `PUT api/epics/{epicId}/assignees` | The same, for an epic. |

A WIP limit on "work in progress per person" needs both `task.setStatus` and
`task.setAssignees`: without the second, assigning someone a task that is
already in progress gets around the limit.

### Members and comments: `ISoloQuery`

- **`MembersAsync(projectId)`** returns the members, pending invites included,
  as core's own members endpoint shows them to anyone who can read the project.
  Emails included, because that endpoint includes them. It reveals nothing a
  member couldn't already see, and there is no instance-wide user list.
- **`CommentAsync(commentId)`** returns one comment, or null when the caller
  can't read what it is on. A `comment` event carries only the id, so this is
  how a handler gets the text, for @mentions or a chat feed.
- **`TaskCommentsAsync(taskId)`** returns a task's thread, oldest first.

### Background work: `ISoloBackground`

Outside a request there is no caller, so `ISoloQuery` returns nothing and there
is no credential to write with. `ISoloBackground` gives scheduled work both, as
a user who said it may.

```csharp
// The endpoint a person uses to switch the feature on: their grant.
[HttpPost("projects/{projectId:guid}/digest")]
[Authorize(Policy = SoloPolicies.ProjectRead)]
public async Task<IActionResult> Enable(
    Guid projectId, [FromKeyedServices("digest")] ISoloBackground background, CancellationToken ct)
{
    await background.GrantAsync(ct);
    // ... save the subscription in your own schema ...
    return NoContent();
}

// Later, from a hosted service.
public sealed class DigestService([FromKeyedServices("digest")] ISoloBackground background) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        foreach (var userId in await SubscribersAsync(stopping))
        {
            var ran = await background.RunAsAsync(userId, async (scope, ct) =>
            {
                var projects = await scope.Query.ProjectsAsync(ct);   // as this user
                await scope.Api.PutAsJsonAsync($"api/tasks/{id}/status", body, Json, ct);  // as this user
            }, stopping);
            // ran is false when the grant is gone: skip them, or tidy up.
        }
    }
}
```

- **A grant can only come from the person.** `GrantAsync` takes no user id; it
  records the caller of the request it runs in, and refuses outside a request,
  for an anonymous caller, and from background work.
- **Core keeps the grants**, so core can revoke them: a super admin signing a
  user out everywhere deletes theirs along with their sessions, and the answer
  reports them against your extension. Purging the extension deletes its grants.
  `RevokeAsync` is for when the last thing that needed one is gone.
- **The work can do what a request from that user could, checked when it
  runs.** A member who has left a project can no longer touch it. It never
  carries super admin: Admin settings' endpoints refuse it, and
  `scope.Caller.IsSuperAdmin` is false.
- **Write through `scope.Api`.** It is core's REST API, authenticated as the
  user, so a change passes authorization, every extension's hooks, the audit log
  and the live broadcast. Each request carries a fresh short-lived credential
  that names the grant, which core re-reads every time, so a revoked grant stops
  the very next call. Core reads and writes enums as strings; serialize with a
  `JsonStringEnumConverter`.
- **Requests from it carry `SoloBackgroundClaims.ExtensionId`**, set by core
  from the grant. An `ISoloAuditContributor` can use it to attribute a change to
  your extension rather than to the person alone.
- **The API is reached over loopback**, at the address the server listens on.
  When that can't be dialled from inside the container, set
  `Extensions__ApiBaseUrl`.

### User topics

A project topic is open to every member of the project, so
`"inbox:{someoneElse}"` on one would be readable by all of them. A user topic
belongs to one person:

```csharp
// Server half.
await live.PublishToUserAsync(recipientId, "inbox", "unread", count.ToString(), ct);

// Client half: the signed-in user's own topic, in every tab they have open.
_inbox = await Live.SubscribeUserAsync("inbox", message => InvokeAsync(ReloadAsync));
```

The hub joins a user topic on behalf of whoever is signed in on the connection,
so there is no way to ask for someone else's. Messages carry an empty
`ProjectId`. There is no presence on a user topic. Otherwise it behaves like a
project topic: a nudge, sender left out, best-effort.

### Task cards and board columns

| Attribute | Where it renders | Parameters |
|---|---|---|
| `[TaskCardExtension(Order = 0)]` | Every task card on a board, in its bottom row beside the deadline ring | `Task` (`TaskDto`), `ProjectId` (`Guid`), `CanWrite` (`bool`) |
| `[BoardColumnExtension(Order = 0)]` | Beside each column's title in the board header, after core's count | `Board` (`BoardDto`), `Status` (`TaskState`), `Tasks` (`IReadOnlyList<TaskDto>`), `CanWrite` (`bool`) |

- **A card contribution renders once per card**, and a board can show
  hundreds. Load per board, not per card: one fetch shared through a static
  cache in your assembly, keyed by board id and refreshed from a live topic.
- **Keep it to a glance:** a badge, a chip, a count. Render nothing when there is
  nothing to say.
- **The card opens the drawer when clicked**, so anything interactive inside
  your contribution needs `@onclick:stopPropagation`.
- **`Tasks` is the column across all the board's lanes**, folded finished cards
  included, the same set core counts. A WIP badge needs no request.
- **The phone layout hides the board header**, so column contributions don't
  show there.

### The task menu: `ISoloTaskMenuContributor`

Core shows a menu for a task when someone right-clicks its card, and from a ⋯
button in its drawer, which is how a touchscreen reaches it. An extension adds
items with a class, not a component:

```csharp
[TaskMenuContributor(Order = 0)]
public sealed class SprintMenu(ISoloClientApi api, ISoloTaskPicker picker) : ISoloTaskMenuContributor
{
    public ValueTask<IReadOnlyList<ContextMenuItem>> ItemsAsync(TaskMenuContext context, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ContextMenuItem>>(context.CanWrite
            ? [new ContextMenuItem("add", "Add to sprint")]
            : []);

    public Task OnChosenAsync(string itemId, TaskMenuContext context) =>
        api.SendAsync(HttpMethod.Post, $"api/ext/sprints/tasks/{context.Task.Id}", null);
}
```

- **The host creates it for each menu** from its own services, so the
  constructor can take `ISoloClientApi`, `ISoloTaskPicker`, `IDialogService` and
  the rest. It is disposed after the menu closes if it is disposable.
- **`ItemsAsync` is awaited before the menu shows.** Answer from what you
  already hold. A contributor that throws or takes longer than a second is left
  out of that menu, and the rest still show.
- **Ids are yours alone.** The host prefixes them, so two extensions can both
  say `"open"`, and `OnChosenAsync` gets your own id back.
- **Core's items come first** ("Open task" on a card), then each contributor's
  as a group of its own. A board only takes over the right click on its cards
  while some extension contributes; with none installed, it stays the browser's.
- **Leave out what the person can't do.** The menu checks nothing, so an item
  that would only fail is worse than no item.

### Project and board settings

| Attribute | Where it renders | Parameters |
|---|---|---|
| `[ProjectSettingsSection("Title", Order = 0)]` | A project's Settings tab, below core's fields and above the danger zone | `Project` (`ProjectSummaryDto`), `CanWrite` (`bool`) |
| `[BoardSettingsSection("Title", Order = 0)]` | A board's settings dialog, below its name, alias and icon | `Board` (`BoardDto`), `CanWrite` (`bool`) |

Core draws the title. Your section saves through your own endpoint, on its own
control or as it changes, and that endpoint checks for itself: the Settings tab
only shows to owners, but that is a hiding place, not a guard. Use
`SoloPolicies.ProjectOwner` or `ProjectWrite` on a route carrying the project or
board id.

The board dialog's Save button saves core's fields only. Per-board values fit
the board's extension field bag (`IExtensionFieldStore` with
`ExtensionEntity.Board`); saving one moves the board's version, and the dialog
carries that forward rather than answering the person's own change with a
conflict.

### The app bar

`[AppBarExtension(Order = 0)]` marks a component rendered in the app bar,
before the connection indicator and the account menu, on every page once
signed in. It receives no parameters. It is the one place always on screen,
which makes it the place for state that changes while the person is elsewhere:
an unread count, a running timer. Keep it to an icon button with a badge. The
bar is narrow on a phone and every extension shares it. A manifest nav entry is
still the way to a page. Pair it with a user topic to keep the count live.

## Contract 1.9

No new seam. The version moved because `Solo.Shared` gained members, and a
member of a published assembly is part of the contract whether or not an
extension was meant to use it: `AuthConfigDto` and the instance settings DTOs
carry the instance's `DisplayName` and `Tagline`, and `InstanceBranding` holds
their limits and defaults. Core's own client and API are what use them.

Nothing an extension does today needs 1.9, so leave your ranges where they
are. There is still no seam for reading the instance's name from a client
page — an extension's `<PageTitle>` that ends in "Solo" stays that way on a
named instance.


## Contract 1.10: collaborative content

The server can receive messages from project-topic subscribers. Register one scoped
`ISoloLiveMessageHandler` with `services.AddLiveMessageHandler<MyHandler>()`.
A second handler from the same extension is a load failure. A topic's prefix before
its first colon is its owner: only that extension's handler receives the message.
Disabled extensions and topics without handlers refuse sends; user topics cannot
receive client messages. Use `LiveMessage.Sender` for identity and project role,
including `IsProjectOwner`, rather than HTTP request state. `ConnectionId` is an
opaque key for bounded multipart buffers or leases; it changes on reconnect and
is not a credential. Core refreshes those
values when membership changes. Read members can send messages; the handler must
refuse edits while permitting reads and sync.

`ILiveMessageReply.ToSenderAsync` answers just that connection;
`ToOthersAsync` reaches every other connection on the topic, including other tabs
of the same user. Neither call is automatic. Messages arrive as `TopicMessage`
with the sender's `ActorUserId`. Handlers have five seconds and must honor
cancellation. Do not retain a handler, reply, or invocation-scoped service.

Clients use `ISoloLiveTopics.SendAsync(projectId, topic, kind, data, ct)`.
`LiveSendResult.Accepted` means the handler ran; it does not promise that it saved
anything. Define acknowledgments in your protocol for durable edits. Kinds are
1–64 characters and data is at most 48,000 characters. The hub wire envelope is
limited to 64 KiB, so use ASCII/base64 and allow room for JSON escaping when
splitting payloads. Per-connection defaults are a burst of 200 and 50 tokens per
second (`RateLimiting:Live:Burst` / `TokensPerSecond`), separate from REST limits.
Watch `IsConnected` and `ConnectionChanged`: on reconnect, held topics have been
resubscribed, but messages during the gap must be resynced by your protocol.

The markdown editor discovers classes marked `[MarkdownEditorAction]` implementing
`ISoloMarkdownEditorAction`. `ButtonsAsync` gets one second; `OnChosenAsync` can
open a dialog and returns plain markdown to insert at the cursor, replacing the
selection. Null inserts nothing. `MarkdownEditorContext` identifies the project,
entity, surface (task description, epic description, or comment), and selection.
The editor refuses inserts beyond its character limit. Button IDs are isolated
between contributors. The host constructs contributors with constructor injection
and disposes them with their component.

`[MarkdownLinkProvider]` classes implement `ISoloMarkdownLinkProvider`.
`Claims` must be synchronous and cheap. `LabelsAsync` receives all claimed safe
links in a batch, with a one-second deadline. Return plain-text labels keyed by
URL. Images, unsafe destinations, and omitted URLs keep their original rendering;
failures leave written labels intact. Core renders immediately, then replaces
labels. **Core calls on every render; providers own caching.** When uninstalled,
links still render with their stored labels.

`ISoloClientApi.GetBytesAsync` returns `SoloBinary(Content, ContentType)`;
`SendBytesAsync<T>` sends raw bytes with a media type and reads a JSON response.
Both share core's sign-in credential and `SoloApiException` handling. Its optional
`RetryAfter` delay preserves an HTTP `Retry-After` header (seconds or a date), so
bulk uploaders can pace retries after a 429. Fetch
members-only images through this API and create/revoke object URLs in JavaScript.
Both browser and desktop CSPs permit `blob:` images; uploaded files still need
an isolated response policy and authorization on every fetch.
