---
title: Server half
lede: The server half is a .NET class library that runs in the Solo process. It holds your endpoints, your data and your access to core data.
---

## The project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="HunterAhlquist.Solo.Extensibility" Version="1.10.0" />
  </ItemGroup>
</Project>
```

`HunterAhlquist.Solo.Extensibility` is the only Solo reference. Do not reference `Solo.Api` or `Solo.Data`.

## The entry point

A server assembly has exactly one class that implements `ISoloExtension`. The class needs a public constructor with no parameters.

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
        services.AddScoped<MilestoneService>();
    }
}
```

Solo calls `ConfigureServices` one time, before it builds its service container. Register your own services here in the usual way.

`IExtensionContext` gives you:

| Member | Content |
|---|---|
| `Id` | The ID from the manifest. |
| `Manifest` | The full manifest. |
| `Schema` | The schema of the extension, or `null` when the manifest declares none. |
| `ConnectionString` | The connection string of the Solo database. |
| `LoggerFactory` | A factory for loggers. |

If `ConfigureServices` throws an exception, Solo records the failure and disables the extension. The instance still starts.

## Endpoints

Write MVC controllers. Solo finds them in your assembly.

```csharp
[ApiController]
[Route("milestones")]
public sealed class MilestonesController(RoadmapDbContext db) : ControllerBase
{
    // GET /api/ext/roadmap/milestones/{boardId}
    [HttpGet("{boardId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<IReadOnlyList<string>> List(Guid boardId, CancellationToken ct) =>
        await db.Milestones.Where(m => m.BoardId == boardId).Select(m => m.Name).ToListAsync(ct);
}
```

Solo puts `/api/ext/<id>/` in front of each route. This applies to each `[Route]` value, so an extension cannot replace a core endpoint.

### Authorization

Use the policies in `SoloPolicies`. They are the policies that Solo uses for its own endpoints.

| Policy | Who passes |
|---|---|
| `SoloPolicies.ProjectRead` | Each member of the project. |
| `SoloPolicies.ProjectWrite` | A member with the Read/write role, or an owner. |
| `SoloPolicies.ProjectOwner` | An owner of the project, or a super admin. |
| `SoloPolicies.SuperAdmin` | A super admin. |

A project policy finds the project from the route. The route must contain one of these values:

`projectId`, `projectAlias`, `boardId`, `epicId`, `taskId`, `subTaskId`, `commentId`

Select the attribute as follows:

- **The route contains one of those values.** Use a project policy.
- **The route contains none of them.** Use a plain `[Authorize]`, and read core data through `ISoloQuery`. A project policy cannot find a project on such a route, and refuses each request.
- **No attribute.** Solo applies the same rule as a plain `[Authorize]`.

A plain `[Authorize]` passes only a person who is a user of the instance. A valid sign-in with the identity provider is not sufficient. The identity provider can give a token to each person who has an account there. Solo refuses a person that nobody invited.

> A page that only owners can open does not protect an endpoint. Each endpoint must have its own check.
{: .warning}

## Read core data

Inject `ISoloQuery`. It reads the data of Solo with the project access of the caller.

```csharp
public sealed class RollupService(ISoloQuery query)
{
    public async Task<int> OpenTasksAsync(Guid boardId, CancellationToken ct)
    {
        var tasks = await query.TasksAsync(boardId, ct);
        return tasks.Count(t => t.Status != TaskState.Finished);
    }
}
```

| Method | Returns | Since |
|---|---|---|
| `ProjectsAsync()` | The projects that the caller can read. | 1.0 |
| `BoardsAsync(projectId)` | The boards of a project. | 1.0 |
| `EpicsAsync(boardId)` | The epics of a board. | 1.0 |
| `TasksAsync(boardId)` | The tasks of a board. | 1.0 |
| `TaskAsync(taskId)` | One task, or `null`. | 1.0 |
| `CanWriteBoardAsync(boardId)` | `true` when the caller can write to the project of the board. | 1.0 |
| `TaskAssigneeIdentitiesAsync(taskId)` | The issuer and subject of each assignee of a task. | 1.2 |
| `CallerAsync()` | The caller: user ID, email, display name, super-admin flag, and the role on a route with a project. | 1.3 |
| `MembersAsync(projectId)` | The members of a project. This includes pending invitations. | 1.8 |
| `CommentAsync(commentId)` | One comment, or `null`. | 1.8 |
| `TaskCommentsAsync(taskId)` | The comments of a task, oldest first. | 1.8 |

When the caller cannot read a project, a method returns an empty list or `null`. It does not throw an exception. Core endpoints do the same.

`ISoloQuery` only reads. To change core data, call the Solo API. From a page, use [`ISoloClientApi`](client.md#call-the-api). From scheduled work, use [`ISoloBackground`](hooks-and-events.md#background-work).

> Do not inject `SoloDbContext`. It does not apply project access, so your code would see each project on the instance. It also ties your extension to the tables of Solo, which can change.
{: .danger}

`TaskAssigneeIdentitiesAsync` is for integrations on the server. Do not send the identities to the browser.

## Your own data

An extension that stores data has its own `DbContext` and its own schema.

### 1. Declare the schema

Add `schema` to the [manifest](manifest.md#schema):

```json
"schema": "ext_roadmap"
```

### 2. Write the context

```csharp
public sealed class Milestone
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public Guid BoardId { get; set; }
    public Guid? TaskId { get; set; }
}

public sealed class RoadmapDbContext(DbContextOptions<RoadmapDbContext> options) : DbContext(options)
{
    public DbSet<Milestone> Milestones => Set<Milestone>();

    protected override void OnModelCreating(ModelBuilder b) => b.HasDefaultSchema("ext_roadmap");
}
```

The default schema of the context must be the schema in the manifest.

### 3. Register the context

In `ConfigureServices`:

```csharp
services.AddExtensionDbContext<RoadmapDbContext>(context);
```

Use `AddExtensionDbContext`, not `AddDbContext`. It supplies the connection string, and it puts the migration history table in your schema.

### 4. Create the migrations

You create migrations at build time, as for a normal EF Core project. The tools need a design-time factory:

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

Add the design package to the project:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.13">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

Then add a migration:

```bash
dotnet ef migrations add InitialRoadmap --project Roadmap --startup-project Roadmap
```

Solo applies your migrations at startup, after its own. If a migration fails, Solo disables only your extension. Your endpoints then answer with status `503` and the reason.

### Rules for your tables

- **No foreign keys into core tables.** A foreign key from your schema into a table of Solo would stop Solo from changing that table. Store the ID of a core record as a plain `Guid`.
- **Expect deleted records.** A task that your row refers to can be gone. Handle that case.
- **Never add types to `SoloDbContext`.** Solo creates its migrations from a fixed model. An added type breaks them.

## Fields on core records

To add a value to a task, an epic or a board, you do not need a table. Each of these records has a JSON field for extensions. Your extension can read and write only its own part of that field.

Inject `IExtensionFieldStore` with your extension ID as the key:

```csharp
public sealed class EstimateController(
    [FromKeyedServices("estimates")] IExtensionFieldStore fields) : ControllerBase
{
    [HttpPut("tasks/{taskId:guid}/estimate")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Set(Guid taskId, EstimateRequest request, CancellationToken ct)
    {
        try
        {
            await fields.SetVersionedAsync(
                ExtensionEntity.Task, taskId, new { hours = request.Hours }, request.Version, ct);
            return NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
    }
}
```

| Method | Purpose |
|---|---|
| `GetAsync<T>(entity, id)` | Read your value, or `null`. |
| `SetAsync<T>(entity, id, value)` | Write your value. |
| `SetVersionedAsync<T>(entity, id, value, version)` | Write your value only if the record still has that version. It throws `DbUpdateConcurrencyException` on a conflict. |
| `ClearAsync(entity, id)` | Delete your value. |

`entity` is `ExtensionEntity.Task`, `ExtensionEntity.Epic` or `ExtensionEntity.Board`.

A write to a field has these effects:

- It changes the version of the record.
- It goes into the audit log, as one change of the full field.
- Solo sends a live update to the open boards.

When an administrator deletes the data of your extension, Solo also deletes your values from these fields.

## Who is the caller

`ISoloQuery.CallerAsync()` returns an `ExtensionCaller`:

| Property | Content |
|---|---|
| `UserId` | The Solo ID of the user. `Guid.Empty` for a request with no sign-in. |
| `Email`, `DisplayName` | The profile of the user. |
| `IsSuperAdmin` | `true` for a super admin. |
| `Role` | The role in the project, when the route names a project. |
| `IsProjectOwner` | `true` for an owner, when the route names a project. |

Use it to record who created a row in your own tables.

## Authentication schemes and middleware

`ConfigureServices` runs before Solo builds the application. You can register an authentication scheme and middleware there:

```csharp
services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MyHandler>("my-scheme", null);
services.AddSingleton<IStartupFilter, MyMiddlewareFilter>();
```

An extension needs this when it issues its own type of credential. The MCP extension does this for its tokens. See [`extensions/Mcp/Solo.Mcp/Auth`](https://github.com/HunterAhlquist/Solo/tree/main/extensions/Mcp/Solo.Mcp/Auth).

If your extension issues credentials, also register a [credential issuer](hooks-and-events.md#credentials-that-you-issue). Then **Sign out everywhere** revokes them.
