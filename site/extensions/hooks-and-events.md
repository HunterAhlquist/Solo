---
title: Hooks and events
lede: React to changes in Solo. A hook runs before a core write and can refuse it. An event handler runs after a change. Live updates and background work let your extension act on its own.
---

## Which one to use

| You want to | Use |
|---|---|
| Refuse or change a core write | A [hook](#hooks) |
| Do work after a core change | An [event handler](#events) |
| Tell open pages that your data changed | [Live updates](#live-updates) |
| Do work on a schedule, as a user | [Background work](#background-work) |
| Add a line to audit entries | An [audit contributor](#audit-annotations) |
| Revoke your tokens when an admin signs a user out | A [credential issuer](#credentials-that-you-issue) |

## Hooks

A hook runs around a core write operation, such as a status change of a task. Each operation has a fixed name. [Reference](reference.md#operations) lists the names.

```csharp
public sealed class MilestoneFreezeHook(RoadmapDbContext db) : ISoloOperationHook
{
    public IReadOnlySet<string> Operations { get; } =
        new HashSet<string> { Solo.Extensibility.Operations.TaskSetStatus };

    public int Order => 10;

    public async ValueTask<HookOutcome> OnBeforeAsync(OperationContext context, CancellationToken ct)
    {
        if (context.Target.TaskId is not { } taskId)
        {
            return HookOutcome.Proceed;
        }

        return await db.Milestones.AnyAsync(m => m.TaskId == taskId && m.IsFrozen, ct)
            ? HookOutcome.Reject(409, "Milestone frozen", "This task's milestone is frozen.")
            : HookOutcome.Proceed;
    }
}
```

To use a hook, do these two steps:

1. Register it in `ConfigureServices`:

   ```csharp
   services.AddOperationHook<MilestoneFreezeHook>();
   ```

2. List its operations in the manifest:

   ```json
   "hooks": ["task.setStatus"]
   ```

Solo ignores a hook for an operation that the manifest does not list.

### Before the write

`OnBeforeAsync` runs after authorization and before the core action. It returns one of two results:

- `HookOutcome.Proceed` lets the operation continue.
- `HookOutcome.Reject(status, title, detail)` stops it. The caller gets that status and text. Use a `4xx` status.

`OperationContext` gives you:

| Member | Content |
|---|---|
| `Operation` | The name of the operation. |
| `Caller` | The signed-in caller. |
| `Target` | The IDs from the route: `ProjectId`, `BoardId`, `EpicId`, `TaskId`, `SubTaskId`. Each can be `null`. |
| `Arguments` | The arguments of the action, by parameter name. |
| `Request<T>()` | The request body, when the action has one. |

A hook can change a value in `Arguments`. Later hooks and the action then get the changed value.

### After the write

`OnAfterAsync` is optional. It runs after the action, with the status code and the response body.

Before this method runs, Solo saves the change and sends it to the open boards. An exception in `OnAfterAsync` cannot undo the change.

### Rules for hooks

- **The record can be absent.** Solo looks for the task or epic in the action, after your hook. Do not assume that the ID in `Target` exists.
- **A hook can only restrict.** Hooks run after authorization. A hook cannot give access that the caller does not have.
- **A slow or failed hook stops the request.** If `OnBeforeAsync` throws an exception or takes longer than 2 seconds, the request fails. The error names your extension. To change this, see [failOpen](manifest.md#failopen).
- **Order.** Hooks run in ascending `Order`. Hooks with the same `Order` run by extension ID.
- **Two operations can do one job.** A limit on work in progress for each person needs `task.setStatus` and `task.setAssignees`. With only the first, a person can get an assignment to a task that is already in progress.

## Events

An event handler runs after Solo saved a change. It cannot refuse the change.

```csharp
public sealed class TaskRollupHandler(RoadmapDbContext db, ISoloQuery query) : ISoloEventHandler
{
    public IReadOnlySet<string> EntityTypes { get; } = new HashSet<string> { "task" };

    public async ValueTask HandleAsync(SoloDomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent.Kind != ChangeKind.Deleted)
        {
            return;
        }

        // A move to a different board also sends Deleted for the old board.
        if (await query.TaskAsync(domainEvent.EntityId, ct) is null)
        {
            await db.Milestones
                .Where(m => m.TaskId == domainEvent.EntityId)
                .ExecuteDeleteAsync(ct);
        }
    }
}
```

Register it in `ConfigureServices`:

```csharp
services.AddEventHandler<TaskRollupHandler>();
```

`EntityTypes` selects the types that the handler gets. An empty set selects all types. The types are:

`project`, `board`, `epicGroup`, `epic`, `task`, `subtask`, `label`, `comment`

`SoloDomainEvent` gives you:

| Member | Content |
|---|---|
| `EntityType`, `EntityId` | The record that changed. |
| `Kind` | `Created`, `Updated` or `Deleted`. |
| `ChangedFields` | The names of the fields that changed. |
| `ActorUserId` | The user who made the change. |
| `ProjectId`, `BoardId` | The project and the board, when the record has them. |

An event contains IDs only. To get the text of a comment, call `ISoloQuery.CommentAsync`.

### Rules for events

- **Delivery is not guaranteed.** Solo delivers events in its own process and does not store them. If the instance stops at the wrong time, a handler does not get the event. Do not build a feature that must get each event.
- **A failure stays in your handler.** Solo logs an exception and continues. An exception cannot fail the core write.
- **Time limit.** Solo stops waiting for a handler after 10 seconds.
- **A move between boards sends `Deleted`.** When a task moves to a different board, Solo sends a `Deleted` event for the old board. Call `ISoloQuery.TaskAsync` to learn if the task still exists.

## Live updates

Solo sends its own changes to open boards. To keep your pages current when your own data changes, publish on a **topic**. Contract 1.4 added topics.

A topic is a short text key that you select, such as `plans:3f2a…`. Put a prefix of your own at the start, so two extensions cannot use the same key.

### Publish from the server half

Inject `ISoloLiveUpdates`. Publish after you save the change.

```csharp
public sealed class PlansController(PlansDbContext db, ISoloLiveUpdates live) : ControllerBase
{
    [HttpPut("projects/{projectId:guid}/plans/{id:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Update(
        Guid projectId, Guid id, UpdatePlanRequest request, CancellationToken ct)
    {
        // Save the change here.

        await live.PublishAsync(projectId, $"plans:{id:N}", "changed", ct: ct);
        return NoContent();
    }
}
```

### Subscribe in the client half

Inject `ISoloLiveTopics`. Dispose the subscription when the component goes away.

```csharp
[Inject] private ISoloLiveTopics Live { get; set; } = null!;

private IAsyncDisposable? _live;

protected override async Task OnInitializedAsync()
{
    _live = await Live.SubscribeAsync(Project.Id, $"plans:{PlanId:N}",
        message => InvokeAsync(ReloadAsync));
}

public async ValueTask DisposeAsync()
{
    if (_live is not null)
    {
        await _live.DisposeAsync();
    }
}
```

### Rules for topics

- **A message is a signal, not data.** Send what changed. The page then gets the new data from your API, which checks access. `PublishAsync` does not check access.
- **Only members can subscribe.** A person can subscribe to the topics of a project only with read access to that project.
- **The sender gets no message.** The page that made the change does not get its own message. It has the change already.
- **Delivery is not guaranteed.** A page must still work when it gets no message.
- **Reconnection.** After a lost connection, Solo subscribes to the same topics again.
- **Callbacks.** A callback runs outside the render cycle. Call `InvokeAsync` in it.
- **Limits.** A topic has 200 characters or less. One connection can hold 64 topics.

### Presence

Presence shows who else is on a topic. It is optional.

```csharp
_live = await Live.SubscribeAsync(Project.Id, topic,
    message => InvokeAsync(ReloadAsync),
    others => InvokeAsync(() => { _others = others; StateHasChanged(); }));

await Live.SetPresenceAsync(Project.Id, topic, PresenceMode.Viewing);
```

- `SetPresenceAsync` reports the current user as `Viewing` or `Editing`. `PresenceMode.None` clears the report.
- The second callback gets the other people on the topic, one entry for each user. The current user is not in the list.
- When you dispose the subscription, Solo clears the presence.

### User topics

Each member of a project can subscribe to each topic of that project. For data that belongs to one person, such as a count of unread messages, use a user topic. Contract 1.8 added user topics.

```csharp
// Server half
await live.PublishToUserAsync(recipientId, "inbox", "unread", count.ToString(), ct);
```

```csharp
// Client half
_inbox = await Live.SubscribeUserAsync("inbox", message => InvokeAsync(ReloadAsync));
```

Solo subscribes the signed-in user to their own topic. A page cannot subscribe to the topic of a different user. A user topic has no presence.

## Background work

Outside a request, your code has no caller. `ISoloQuery` then returns no data, and you have no credential for the API.

`ISoloBackground` lets scheduled work run as a user who permitted it. Contract 1.8 added it. Inject it with your extension ID as the key.

### 1. Get the permission of the user

Call `GrantAsync` in a request that the user makes, such as the request that enables your feature.

```csharp
[HttpPost("projects/{projectId:guid}/digest")]
[Authorize(Policy = SoloPolicies.ProjectRead)]
public async Task<IActionResult> Enable(
    Guid projectId, [FromKeyedServices("digest")] ISoloBackground background, CancellationToken ct)
{
    await background.GrantAsync(ct);
    // Save the subscription in your own schema.
    return NoContent();
}
```

`GrantAsync` has no user parameter. It records the caller of the current request. It fails outside a request and for a caller with no sign-in.

### 2. Do the work as that user

```csharp
public sealed class DigestService(
    [FromKeyedServices("digest")] ISoloBackground background) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        foreach (var userId in await SubscribersAsync(stopping))
        {
            var ran = await background.RunAsAsync(userId, async (scope, ct) =>
            {
                var projects = await scope.Query.ProjectsAsync(ct);
                // Change core data through scope.Api.
            }, stopping);

            if (!ran)
            {
                // The user has no grant now. Delete the subscription.
            }
        }
    }
}
```

`RunAsAsync` returns `false` when the user has no grant. The `scope` gives you:

| Member | Content |
|---|---|
| `Caller` | The user that the work runs as. |
| `Query` | An `ISoloQuery` for that user. |
| `Api` | An `HttpClient` for the Solo API, authenticated as that user. |
| `Services` | A service scope for your own services. |

### Rules for background work

- **The work has the access of the user at that time.** If the user left a project, the work cannot reach that project.
- **The work is never a super admin.** `scope.Caller.IsSuperAdmin` is `false`, and the endpoints of the Admin settings refuse the work.
- **Write through `scope.Api`.** A change then goes through authorization, the hooks of each extension, the audit log and live updates.
- **Enums are text.** The Solo API reads and writes enum values as text. Use a `JsonStringEnumConverter` when you serialize a request.
- **Solo owns the grants.** **Sign out everywhere** deletes the grants of a user. The deletion of your extension data deletes all grants of your extension. Call `RevokeAsync` when your feature no longer needs a grant.
- **Address.** `scope.Api` connects to the address that Solo listens on. If the container cannot connect to that address, the administrator sets `Extensions__ApiBaseUrl`.

A request from background work has the claim `SoloBackgroundClaims.ExtensionId`. An audit contributor can use it to mark the change as the work of your extension.

## Audit annotations

An `ISoloAuditContributor` adds a line of text to core audit entries. The MCP extension uses it to mark changes as **LLM via MCP**. Contract 1.2 added it.

| Member | Purpose |
|---|---|
| `ShouldRecord` | Return `true` for a request that needs an annotation. |
| `RecordAsync(entries, connection, transaction, ct)` | Solo calls this with the new audit IDs before it commits. Write your rows with the connection and the transaction that you get. |
| `ReadAsync(auditIds, ct)` | Return the text for the audit IDs of one page. |

Rules:

- In `RecordAsync`, write only to your own schema.
- Do not commit or dispose the connection or the transaction. They belong to Solo.
- If `RecordAsync` fails, Solo does not save the core change.
- If `ReadAsync` fails, Solo logs the failure and shows the audit page with no annotation.

[`McpAuditContributor`](https://github.com/HunterAhlquist/Solo/tree/main/extensions/Mcp/Solo.Mcp) in the MCP extension is the example.

## Credentials that you issue

If your extension issues tokens or keys for users, register an `ISoloCredentialIssuer`. Contract 1.7 added it.

When a super admin selects **Sign out everywhere** for a user, Solo revokes its own sessions. Solo then calls each credential issuer. Without an issuer, your tokens stay valid.

```csharp
public sealed class TokenIssuer(MyDbContext db) : ISoloCredentialIssuer
{
    public Task<int> RevokeAllAsync(CredentialHolder holder, CancellationToken ct) =>
        db.Tokens
            .Where(t => t.Issuer == holder.Issuer && t.Subject == holder.Subject && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
}
```

Register it in `ConfigureServices`:

```csharp
services.AddCredentialIssuer<TokenIssuer>();
```

Rules:

- **Return the number of revoked credentials.** Solo shows the number to the admin.
- **A failure is visible.** If the method throws an exception, Solo tells the admin that your extension did not revoke its credentials. The sessions of Solo stay revoked.
- **Find the user by issuer and subject.** An email address can change. The issuer and subject cannot.
- **Put only the issuer and subject on a principal.** When your authentication handler creates a principal, give it the `iss` and `sub` claims only. Solo treats an `email` claim as the current, verified address from the identity provider.
