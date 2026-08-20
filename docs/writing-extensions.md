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
