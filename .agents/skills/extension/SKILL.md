---
name: extension
description: Build a Solo extension — a self-contained package adding endpoints, data, hooks or client pages. Use when asked to add an extension, plugin, add-on or module, or when deciding whether a feature belongs in core or an extension.
---

# Building a Solo extension

**Read [docs/writing-extensions.md](../../../docs/writing-extensions.md) before writing
code.** It is the authoring recipe: project setup, manifest, entry point, data,
endpoints, hooks, events, client pages, packaging, and a table of common
mistakes. `docs/extension-plan.md` is the design record explaining *why* the
system is shaped this way — not what you need in order to build one.

## The failure this exists to prevent

An extension is **its own project** and **modifies no core project**. Its server
half references `Solo.Extensibility` and nothing else from the solution; its
client half references `Solo.Shared` and MudBlazor.

The test is removability: uninstall the package and the instance loses that
feature and nothing else. Where the folder lives is incidental — `src/Solo.Mcp`
sits alongside core and is still a proper extension.

The tempting path is wrong: adding a controller to `Solo.Api`, an entity to
`Solo.Data`, and a page to `Solo.Client` is fewer steps and it *works* — it just
isn't an extension, and it can't be uninstalled, versioned, or shipped
separately. Converting it afterwards is a rewrite, not a refactor, because the
seams differ at every layer.

If you are editing `Solo.Api`, `Solo.Data`, `Solo.Client`, `Solo.Shared` or
`Solo.Extensions`, stop and reconsider.

## Decide core vs extension first

Build it in **core** when every instance needs it, it changes the Kanban model
itself, or it refines something already there. Build it as an **extension** when
it's optional, workflow-specific, sits on top of the existing model, or would be
sensible to uninstall. **If the user said "extension", it's an extension** — don't
start in core intending to extract later.

## The constraints that fail silently

- Entity types **never** go into `SoloDbContext` — extensions own a separate
  context in their own Postgres schema. Contributing one breaks core migrations
  permanently.
- **Never inject `SoloDbContext`.** It bypasses `ProjectAccessHandler`, so the
  extension sees every project on the instance. Use `ISoloQuery`, pre-scoped to
  the caller.
- The schema name is **derived** — `ext_` + id with hyphens as underscores. The
  loader rejects anything else.
- Routes are rewritten to `/api/ext/{id}/…` whatever your `[Route]` says.
- The project authorization policies resolve a project **from route values**. An
  endpoint with no project/board/task id in its route must use a plain
  `[Authorize]` and scope reads through `ISoloQuery`, or every call 500s.
- A **client assembly** cannot reference `Solo.Extensibility` (ASP.NET Core and
  EF don't resolve for `browser-wasm`). Reference `Solo.Shared` plus
  `Components.Web` and MudBlazor, and set `StaticWebAssetsEnabled=false`.
- To call the API from a client page, inject **`ISoloClientApi`**
  (`Solo.Shared.Extensibility`) — the host registers `HttpClient` only for its
  own typed clients. Use `ResolveExternal`, not `Resolve`, for any URL a person
  copies out of the app.
- A nav route with no `{projectAlias}` is an **instance-level** entry (sidebar
  footer, once), not a per-project one.
- **No rescan** — installing or changing an extension requires a restart.

## Reference package

**`src/Solo.Mcp` + `src/Solo.Mcp.Client`** is the worked example: own schema and
migrations, namespaced endpoints, its own authentication scheme, a client page
with static content, packaged by `build/pack-mcp-extension.sh`. Its csproj
comments explain each constraint where it applies. Read it before writing a new
extension.

`tests/Solo.Extension.Fixture/` is the other working package, but it is a *test*
fixture — server-only, and its hook deliberately misbehaves on an
`X-Fixture-Hook` header. Copy its structure, not its behavior.

## Development loop

Skip zipping while iterating — point `Extensions__DevelopmentDirectories__0` at a
package-shaped directory and it loads through the same validation as a zip.
Verify with Admin settings (which lists every extension, its state, and the
reason for any failure), then `\dn` in psql to confirm the schema landed.
