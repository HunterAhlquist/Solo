# Writing a Solo extension

The recipe for building one. For *why* the system is shaped this way — the trust model, the decisions and their alternatives — see [extension-plan.md](extension-plan.md); you do not need it to build an extension.

> **The rule that matters most:** an extension **modifies no core project**. Its server half references `Solo.Extensibility` and nothing else from this solution; its client half references `Solo.Shared` and MudBlazor. If you are editing `Solo.Api`, `Solo.Data`, `Solo.Client`, `Solo.Shared` or `Solo.Extensions`, you are no longer writing an extension.
>
> The test is removability: uninstall the package and the instance loses that feature and nothing else. Where the folder lives is incidental — `src/Solo.Mcp` sits alongside core and is still a proper extension, because no core project knows it exists.

**`src/Solo.Mcp` + `src/Solo.Mcp.Client` is the worked reference.** A complete extension: own schema and migrations, namespaced endpoints, its own authentication scheme, a client page with static content, packaged by `build/pack-mcp-extension.sh` and released by CI. Read it alongside this guide — its csproj comments explain each constraint where it bites.

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
    <ProjectReference Include="../Solo.Extensibility/Solo.Extensibility.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.11">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

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
- Omitting `[Authorize]` entirely still requires an authenticated caller — the fallback policy is fail-closed.

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

**It cannot reference `Solo.Extensibility`** — that carries a FrameworkReference to ASP.NET Core plus EF Core, and neither resolves for `browser-wasm`. Reference `Solo.Shared` instead, take `Components.Web` and MudBlazor as packages, and turn off static web assets (the package's own `content/` folder carries CSS):

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
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.11" />
    <PackageReference Include="MudBlazor" Version="9.8.0" />
    <ProjectReference Include="../Solo.Shared/Solo.Shared.csproj" />
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

`src/Solo.Mcp/Auth/` does exactly this so an MCP credential authenticates early enough for core's `CurrentUserMiddleware` to resolve the user from it. Prefer this over post-configuring core's OIDC forward selector by scheme name, which couples the package to core's setup.

## 11. Package and install

```
roadmap.zip
├── extension.json
├── server/Roadmap.dll        + private dependencies
├── client/Roadmap.Client.dll + private dependencies
└── content/                  optional static assets
```

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

**Read `src/Solo.Mcp` for a real one.** It is a complete extension — schema, migrations, endpoints, its own auth scheme, a client page, static content, CI packaging — and its csproj comments explain each constraint where it applies.

`tests/Solo.Extension.Fixture/` is the other working package, but it is a *test* fixture: server-only, and its hook deliberately misbehaves on an `X-Fixture-Hook` header to exercise reject/throw/timeout paths. Copy its structure if you like; don't copy that behavior.

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
client half) to use anything in this section. `src/Solo.Roadmap` and
`src/Solo.Roadmap.Client` use every addition here (calling `task.setSchedule`
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
