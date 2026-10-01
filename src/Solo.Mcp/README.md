# Solo.Mcp

The MCP server, packaged as a Solo extension. An LLM client that speaks the
Model Context Protocol can browse projects and boards, create, edit and move
tasks, and organize epics into groups — acting as the person whose token
authorized the call.

Everything the feature needs is in this project and its client half
([Solo.Mcp.Client](../Solo.Mcp.Client)): the credential, the schema and
migrations, the endpoints, and the page people use. Nothing about MCP exists in
`Solo.Api`, `Solo.Data` or `Solo.Client`. Delete the package and the instance
loses MCP access and nothing else — including the sidebar entry, which comes
from the manifest.

## Layout

```
extension.json      manifest: id "mcp", schema "ext_mcp", both assemblies, nav
Auth/               the solomcp_ token, its authentication scheme, and the
                    startup filter that runs the scheme before core resolves
                    the user
Controllers/        POST /api/ext/mcp (protocol), /api/ext/mcp/tokens (management)
Data/               McpDbContext + its migrations, in schema ext_mcp
Protocol/           JSON-RPC dispatch, the tool catalog and the prompt catalog
Skills/             the workflow guides, embedded in the assembly and served
                    as MCP prompts
Rest/               the loopback hop back into Solo's REST API
content/            static files served at /api/extensions/mcp/content/
```

The client half is a separate project because it is a different runtime: a Razor
class library the browser downloads and the host's router picks up. See its
[README](../Solo.Mcp.Client/README.md).

## Two decisions worth knowing before changing anything

**Tool calls re-enter the REST API over HTTP** rather than touching the
database. Every core write path is a controller action wrapped in things that
are not the action — project authorization, the `[SoloOperation]` hook filter
that lets another extension veto a move, optimistic concurrency, the audit
interceptor, the SignalR broadcast. Reaching around the pipeline would mean
re-implementing all of it, and an MCP write that silently skipped another
extension's hook would be a hole nobody would find until it mattered. The cost
is one loopback request per REST call; the benefit is that an MCP tool call is
correct by construction. Set `Mcp:ApiBaseUrl` when the app's own listen address
is not dialable as-is.

**Authentication runs from an `IStartupFilter`, at the front of the pipeline.**
Core's `CurrentUserMiddleware` resolves the user from `HttpContext.User` before
authorization, and every project policy reads what it produced — so a principal
established any later would be invisible to all of it, and the REST calls the
tools make would arrive anonymous. The alternative was reaching into core's OIDC
options to chain their forward selector, which couples this package to core's
scheme configuration for no gain.

## Building the package

```bash
build/pack-mcp-extension.sh
```

Produces `artifacts/solo-mcp-<version>.zip`: `extension.json`,
`server/Solo.Mcp.dll`, `client/Solo.Mcp.Client.dll` and `content/`. Nothing
else — the host provides `Solo.Shared`, `Solo.Extensibility`, MudBlazor and the
framework, on both sides. The script refuses to run if the manifest and either
csproj disagree about the version; bump all three together.

CI builds this package on every pull request. For a `server-v*` tag, it attaches
the package to the GitHub release only when the MCP sources, shared contracts,
or packaging script changed since the previous server release.

## Migrations

Scaffolded here, applied by the host at startup (gated on `Database__AutoMigrate`
like core's):

```bash
dotnet ef migrations add <Name> --project src/Solo.Mcp --startup-project src/Solo.Mcp
```

A failed migration disables this extension only; its endpoints then answer 503
naming the reason, and Admin settings shows it.

## Epic groups (1.1.0)

These tools require a Solo instance with the Epic Groups REST endpoints:

| Tool | Purpose |
|---|---|
| `list_epic_groups` | List a board's groups, titles, ranks, and versions. |
| `create_epic_group` | Add an empty group at the end of the board. |
| `update_epic_group` | Rename a group. |
| `delete_epic_group` | Remove a group while keeping its epics and tasks. |
| `rerank_epic_group` | Move a group among groups and ungrouped epics. |
| `set_epic_group` | Move an epic into, between, or out of groups, or reorder it. |

All take `boardId`; operations on an existing group also take `groupId`.
`set_epic_group` takes `epicId` and requires `groupId`, with an explicit JSON
`null` meaning ungroup. `list_epics` returns each epic's `groupId`; combine that
with `list_epic_groups` to reconstruct the hierarchy.

For `set_epic_group`, omitting both `precedingId` and `followingId` appends the
epic to its destination. Passing null neighbors explicitly moves it to the
front. `rerank_epic_group` moves to the front when neither neighbor is set.
Neighbors must be in the destination scope: groups or ungrouped epics at board
level, and epics within a group. When both neighbors are given, `precedingId`
takes priority, matching the REST API.

Updates, rank changes, moves, and deletion fetch the current version before
writing. A concurrent edit produces a conflict rather than being retried or
overwritten. Group deletion returns `{ "deleted": true, "groupId": "…" }`;
its epics take the group's former position in their existing order.

Build the updated archive with `build/pack-mcp-extension.sh`, install it, and
restart Solo. Reconnect MCP clients to refresh their tool catalog.

## LLM work and audit attribution (1.2.0)

The task drawer offers **Work for an LLM** when at least one current assignee
has an active MCP token. It defaults to false. Project writers can change it;
readers can see it. This is metadata only: it does not launch an agent, claim a
task, change its status, or grant additional access.

The flag is stored under the MCP key in the task's extension field bag, with
the eligible assignee ids at the time it was enabled. Reads intersect those ids
with current assignments and active MCP access. Reassigning the task to someone
else does not transfer the flag to their agent. Revoked access is excluded;
restoring access for an originally flagged assignee makes their work visible again.

Agent loops can use `list_llm_tasks` (board id), `get_task_llm_work` (task id or
serial), and `set_task_llm_work` (task id or serial, `llmWork`). List results
include the task, status, and flagged assignee ids; loops should select work for
the intended user and status. The REST equivalents are
`GET /api/ext/mcp/tasks/boards/{boardId}/llm-work` and
`GET|PUT /api/ext/mcp/tasks/{taskId}/llm-work`. PUT requires `llmWork` and the
current task `version`; a stale write returns 409.

Audit entries made using MCP access retain the user's identity and add the
annotation **LLM via MCP**, including tool calls that re-enter the REST API. This
identifies use of the MCP credential, not independent detection of a language
model; scripts using the same credential receive the same attribution. Normal
browser actions and older audit entries have no source marker.

This package requires Solo API and client contract 1.2 or later. Its own
`AddAuditAttributions` migration creates `ext_mcp.AuditAttributions`, linking
core audit-entry ids to the MCP token used. The generic host audit hook writes
those links in the same transaction as the core change. Core's data model,
migrations, and model snapshot are unchanged.

Restart after installing the updated package. Removing MCP removes its task UI,
endpoints, and audit annotations. Core audit history remains intact; MCP-owned
attribution data stays in the extension schema until it is purged.

## Assignees and deadlines (1.3.0)

Until now the catalog could read a task's `assigneeIds` but never set them, and
never saw its deadline at all. That left two things an agent could not do on its
own. It could not name the people behind a set of GUIDs, so per-person reporting
was impossible. And because `set_task_llm_work` refuses a task whose assignees
have no MCP access, an agent that created a task had to stop and ask a human to
assign it in the browser before it could flag its own work.

| Tool | Purpose |
|---|---|
| `get_me` | The user this token belongs to — the id an agent assigns to itself. |
| `list_members` | A project's members, so assignee ids resolve to names. |
| `set_task_assignees` | Replace a task's assignees. |

`set_task_assignees` takes `taskId` (id or serial, like every other task tool)
and `userIds`. It replaces rather than adds: anyone left out is unassigned, and
an empty array clears the task, so read `get_task`'s `assigneeIds` first when
adding someone. Assignees must be activated members of the task's project;
core answers 400 naming the offending ids otherwise.

`list_members` rows carry both an `id` and a `userId`, and only `userId`
assigns — the `id` is the membership, not the person. A pending invite has a
null `userId` and cannot be assigned until that person first signs in.

`create_task` and `update_task` now take `deadlineAt`, a calendar date in
`YYYY-MM-DD` form with no time or zone. On `update_task` it follows the same
rule as description and label: omitted leaves the current deadline alone, an
explicit `null` clears it. Anything that is not an ISO date is refused rather
than guessed at, because `01/02/2026` is two different days depending on who
wrote it.

These call core endpoints that have existed since before this package, so the
contract range is unchanged. Rebuild with `build/pack-mcp-extension.sh`,
install, and restart; reconnect MCP clients to refresh their tool catalog.

## Workflow guides as prompts (1.3.0)

`Skills/*.md` are four agent-skill files — YAML frontmatter naming the workflow
and saying when to reach for it, then the procedure — embedded in the assembly
and served as MCP prompts:

| Prompt | For |
|---|---|
| `solo-create-task` | Filing a reported bug or request as a task, with labelled sub-tasks. |
| `solo-implement-task` | Picking up a task flagged for LLM work and moving it to Finished. |
| `solo-summarize-work` | Recommending what to do next, with blockers inferred. |
| `solo-recent-work` | A per-member recap of the last two weeks. |

`prompts/list` returns names, descriptions and arguments; `prompts/get` returns
one guide as a single user message, with any supplied arguments appended under
a `## This request` heading rather than substituted into the prose. Arguments
are declared in `McpPromptCatalog`, not in the frontmatter — they describe the
MCP calling convention, which an agent skill has no notion of.

**Why prompts rather than longer tool descriptions.** A tool description is paid
for on every request and has to stay one line. A prompt is fetched only when it
is used, so it can carry the whole procedure — which board a task belongs on,
which label kinds are legal where, the order a card moves in, and which
questions the data cannot answer.

**Why embedded rather than in `content/`.** That folder is served anonymously,
and structurally has to be: a page links it with a plain `<link>` or `<img>`,
and a subresource fetch carries no Authorization header. A guide should reach
the same people the tools do — holders of a token — and nobody else. Embedding also means `pack-mcp-extension.sh` needs no
change, because the DLL already ships.

### Tool access for clients without prompt support (1.3.1)

Some clients expose Solo's tools to the model but provide no way to call
`prompts/list` or `prompts/get`. Advertising those methods in initialization
instructions does not make them callable. `get_workflow` exposes the same
embedded guides as an ordinary tool, with the available names and arguments in
its input schema:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "get_workflow",
    "arguments": {
      "name": "solo-recent-work",
      "arguments": { "project": "Solo", "days": "14" }
    }
  }
}
```

The tool returns `name` and `instructions` in its text result. It only retrieves
the guide; the assistant then follows it using the data tools. Required prompt
arguments still apply (`solo-implement-task` requires `arguments.task`).

`McpToolCatalog.Instructions`, received on `initialize`, now leads with the
instruction to call `get_workflow` *before* acting on a matching request. For
example, "What work has been done recently in Solo?" should lead to
`get_workflow` with `name: "solo-recent-work"`, then data gathering and the recap.
This is guidance to the assistant, not a server-enforced trigger. Native prompt
methods remain available for clients with prompt menus.

Rebuild with `build/pack-mcp-extension.sh`, install the updated package, restart
Solo, and reconnect MCP clients to refresh their tool catalog and instructions.

The same files are the source for the repo's own `.claude/skills/` entries,
which symlink to them, so a slash command and a local skill cannot drift apart.

## Epics (1.4.0)

The catalog could list epics and move them between groups, but not create,
rename or remove one, so an agent planning work had to stop and ask for the
epic to be made in the browser first.

| Tool | Purpose |
|---|---|
| `create_epic` | Add an epic at the end of a group, or of the board. |
| `update_epic` | Rename an epic or change its description. |
| `delete_epic` | Remove an epic while keeping its tasks. |

All three take `boardId`; `update_epic` and `delete_epic` also take `epicId`.
`create_epic` takes an optional `groupId` and appends the epic there, or after
the board's groups and ungrouped epics when it is omitted — the same placement
as `create_epic_group`. Moving it afterwards is `set_epic_group`'s job.

`update_epic` follows `update_task`: omitted fields keep their value, an
explicit `null` clears the description, and the current version is read first
so a concurrent edit surfaces as a conflict. Core has no single-epic GET, so
the read is the board's epic list, and an `epicId` that is not on the named
board is refused rather than edited.

`delete_epic` returns `{ "deleted": true, "epicId": "…" }`. Its tasks become
ad-hoc (`epicId` null) with everything else intact, exactly as when an epic is
deleted in the browser. Core's delete takes no version, so there is no conflict
check; the same board check is what stops a mispaired id from deleting an epic
somewhere else the caller can write.

These call core endpoints that predate the 1.2 contract floor, so the range is
unchanged. Rebuild with `build/pack-mcp-extension.sh`, install, and restart;
reconnect MCP clients to refresh their tool catalog.

## Board labels (1.4.0)

`list_labels` took a `projectId` and returned the project's labels, but those
are only defaults: each board is stamped with its own copies when it is made,
and a task or sub-task may carry only its board's. Every id the tool returned
was refused by `create_task` and `create_subtask` with a 400. It now takes a
`boardId` and returns that board's labels, which are also the only list a
task's `labelId` resolves against. The project defaults have no tool, since
nothing an agent writes can use them. A client holding the old schema gets a
"boardId is required" error until it reconnects.

## Start dates (1.5.0)

Core 1.3 gave tasks an optional start date, turning a deadline into a date
range. The catalog follows:

| Tool | Change |
|---|---|
| `create_task` | Takes `startAt` (`YYYY-MM-DD`) beside `deadlineAt`. |
| `set_task_schedule` | New. Sets `startAt` and `deadlineAt` together through `PUT api/tasks/{id}/schedule`. |

`set_task_schedule` is read-modify-write like `update_task`: a field left out
keeps its current value and an explicit `null` clears it. A start needs a
deadline and can't fall after it; core answers 400 otherwise, which reaches the
model as a readable tool error. `update_task` still never carries a start —
core's full-replace PUT has no start field, precisely so an older client can't
wipe one — but clearing the deadline there, or moving it before the start,
clears the start too.

The server half now compiles against `CreateTaskRequest.StartAt`, which only
exists from contract 1.3, so `soloApiVersion` rises to `>=1.3`. The client half
touches nothing new and keeps `>=1.2`, so an older desktop shell still shows
the MCP access page. Rebuild with `build/pack-mcp-extension.sh`, install,
restart, and reconnect MCP clients to refresh their tool catalog.
