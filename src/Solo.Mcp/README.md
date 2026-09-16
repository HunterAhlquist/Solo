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
Protocol/           JSON-RPC dispatch and the tool catalog
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
