# Solo.Mcp

The MCP server, packaged as a Solo extension. An LLM client that speaks the
Model Context Protocol can browse projects and boards and create, edit and move
tasks — acting as the person whose token authorized the call.

Everything the feature needs is in this project: its credential, its schema and
migrations, its endpoints. Nothing about MCP exists in `Solo.Api` or
`Solo.Data`. Delete the package and the instance loses MCP access and nothing
else.

## Layout

```
extension.json      manifest: id "mcp", schema "ext_mcp", server assembly
Auth/               the solomcp_ token, its authentication scheme, and the
                    startup filter that runs the scheme before core resolves
                    the user
Controllers/        POST /api/ext/mcp (protocol), /api/ext/mcp/tokens (management)
Data/               McpDbContext + its migrations, in schema ext_mcp
Protocol/           JSON-RPC dispatch and the tool catalog
Rest/               the loopback hop back into Solo's REST API
```

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

Produces `artifacts/solo-mcp-<version>.zip` containing `extension.json` and
`server/Solo.Mcp.dll` — nothing else, because the host provides `Solo.Shared`,
`Solo.Extensibility` and the framework from its own load context. The script
refuses to run if `extension.json` and the csproj disagree about the version;
bump both together.

CI builds this package on every run and attaches it to the GitHub release when a
`server-v*` tag is pushed.

## Migrations

Scaffolded here, applied by the host at startup (gated on `Database__AutoMigrate`
like core's):

```bash
dotnet ef migrations add <Name> --project src/Solo.Mcp --startup-project src/Solo.Mcp
```

A failed migration disables this extension only; its endpoints then answer 503
naming the reason, and Admin settings shows it.
