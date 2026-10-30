# Solo — agent guide

Project management for small game-dev teams. ASP.NET Core + EF Core/Postgres + SignalR, with a Blazor WASM client served by the same host. One container plus Postgres is the whole deployment.

```
src/Solo.Api            host: controllers, SignalR hub, auth, serves the client
src/Solo.Data           EF Core DbContext, entities, migrations, audit interceptor
src/Solo.Shared         DTOs, enums, hub contracts (shared with the client)
src/Solo.Extensibility  the public contract EXTENSIONS compile against
src/Solo.Extensions     the extension host (loader, hook dispatch, registry)
src/Solo.Client         Blazor WASM client
src/Solo.Desktop        Electron shell (serves the published client, proxies /api)
tests/                  Solo.Data.Tests, Solo.Api.Tests, Solo.Client.Tests, plus the harnesses
                        (Solo.Api.Testing, Solo.Client.Testing) and the fixture extension
extensions/Mcp          an EXTENSION, not core — the reference example (Solo.Mcp.slnx)
extensions/Roadmap      an EXTENSION, not core — timelines; uses every 1.3–1.6 seam (Solo.Roadmap.slnx)
samples/HelloExtension  an EXTENSION built from the NuGet contract packages alone (not in Solo.slnx)
templates/              the `dotnet new` template package that scaffolds an extension (not in Solo.slnx)
site/                   the documentation site (Jekyll, published by GitHub Pages); see site/README.md
```

`Solo.slnx` is core and nothing else. Each extension under `extensions/` is its own solution, building from the contract packages rather than from `src/`; only the test projects that run the host reference `src/`, for the package to be loaded into. A new extension in this repository goes there too, as `extensions/<Name>/`.

A `v<version>` tag releases the whole train — server image, desktop installers, changed extension packages — as one GitHub release, and only rebuilds the components whose inputs changed since the previous `v*` tag. The release's `release-manifest.json` is what the updaters read; `docs/releases.md` explains the parts that are easy to break, and `docs/updates.md` the three updaters that consume it (server indicator, electron-updater, in-place extension updates). Pushing a `v*` tag publishes a GHCR image and a public release, so never push one unasked. The retired `server-v*` and `desktop-v*` series are rejected by `build/release-channel.sh`.

`Solo.Extensibility` and `Solo.Shared` are also published as the NuGet packages `HunterAhlquist.Solo.Extensibility` / `HunterAhlquist.Solo.Shared`. Their version is the contract version: bump `build/contract-package.props` together with `ContractVersion.Current` (a test enforces it) and the `HunterAhlquist.Solo.*` references in the sample, in `extensions/` and in `templates/content/` (`build/pack-contract.sh` enforces that). Pushing an `sdk-v<version>` tag publishes them to nuget.org (`.github/workflows/release-sdk.yml`); that is permanent, so never push one unasked.

`templates/` is published as `HunterAhlquist.Solo.Templates`, the `dotnet new` / Visual Studio / Rider template for a new extension. Its version is its own, in `templates/Solo.Templates.csproj`, but its major.minor has to be the contract's. `build/pack-templates.sh` (after `build/pack-contract.sh`) packs it and builds an extension from each combination of its options; run it after changing anything under `templates/`. The template's sources use `SoloExtension-CSharp` where a project name goes and `SoloExtension_CSharp` where a namespace goes — the engine replaces them separately, so don't "fix" one into the other. A `templates-v<version>` tag publishes it through the same workflow, and is just as permanent.

Build and test core: `dotnet build Solo.slnx` / `dotnet test Solo.slnx`. An extension: `build/pack-contract.sh` first — its output in `artifacts/nuget` is the only source `extensions/nuget.config` allows for the contract packages, published copies on nuget.org included — then `dotnet test extensions/Mcp/Solo.Mcp.slnx`. Run the script again after changing the contract. Integration tests need Docker (Testcontainers Postgres). To run and drive the app locally, use the `verify` skill.

---

## Before adding a feature: core or extension?

**Decide this first — before opening any file.** Solo has an extension system, and building a feature into core that should have been an extension is the most common mistake made here.

| Build it in **core** (`src/`) when… | Build it as an **extension** when… |
|---|---|
| Every instance needs it | It's optional, or specific to one team's workflow |
| It changes the Kanban model itself (task states, ranking, labels, membership) | It adds a view, a report, an integration, or automation on top of the existing model |
| It's a fix or refinement to something already in core | It would be sensible to uninstall |
| The user asked for a change to existing behavior | The user said "extension", "plugin", "add-on", or "module" |

**If the user asked for an extension, the answer is an extension.** Do not "start in core and extract it later" — the seams are different and the extraction is a rewrite.

## If you are building an extension, read this first

**Read [docs/writing-extensions.md](docs/writing-extensions.md) before writing code.** It is the authoring recipe; [docs/extension-plan.md](docs/extension-plan.md) is the design record explaining *why* the system is shaped this way, and is not what you need in order to build one.

The one rule that catches the common failure:

> **An extension modifies no core project.** It is its own project — server half referencing the `Solo.Extensibility` contract and nothing else of core, client half referencing `Solo.Shared` and MudBlazor. If you find yourself editing `Solo.Api`, `Solo.Data`, `Solo.Client`, `Solo.Shared` or `Solo.Extensions`, you have stopped writing an extension and started modifying core — back out and reconsider.
>
> The test is removability: uninstall the package and the instance should lose that feature and nothing else. Delete its folder under `extensions/` and core still builds and its tests still pass — no core project knows the extension exists.

**`extensions/Mcp` (`Solo.Mcp` + `Solo.Mcp.Client`) is the reference example.** A complete extension with its own schema and migrations, namespaced endpoints, an authentication scheme, a client page, static content, and a CI-built package. Read it before writing a new one; its csproj comments explain each constraint at the point it applies.

Things that will silently not work if you get them wrong:

- **Never add entity types to `SoloDbContext`.** Core migrations are generated against a committed model snapshot and applied at startup; a contributed entity makes the runtime model disagree with that snapshot permanently. Extensions own a separate `DbContext` in their own Postgres schema.
- **Never inject `SoloDbContext` into extension code.** It bypasses `ProjectAccessHandler`, so the extension sees every project on the instance. Use `ISoloQuery`, which is pre-scoped to the caller.
- **The schema name is derived, not chosen.** It must be `ext_<id>` with hyphens as underscores; the loader rejects anything else.
- **Routes are rewritten to `/api/ext/{id}/…`** whatever your `[Route]` says. Don't fight it.
- **Extension endpoints without a project id in the route can't use the project policies** — they resolve the project from route values. Use a plain `[Authorize]` and scope reads through `ISoloQuery`.
- **A client assembly cannot reference `Solo.Extensibility`** — it carries a FrameworkReference to ASP.NET Core plus EF Core, and neither resolves for `browser-wasm`. Client halves reference `Solo.Shared` (+ `Microsoft.AspNetCore.Components.Web` and MudBlazor as packages) and set `StaticWebAssetsEnabled=false`.
- **To call the API from a client page, inject `ISoloClientApi`** (`Solo.Shared.Extensibility`). It is the host's own client, so the extension shares its credential and base address. Use `ResolveExternal` — not `Resolve` — for any URL a person copies out of the app, or the desktop shell hands them `localhost:41321`.
- **A nav entry whose route has no `{projectAlias}` is instance-level**, rendered once in the sidebar footer rather than per project.
- **There is no rescan.** Installing or changing an extension needs a restart.

## Conventions

- Comments explain *why*, not what. Match the density and voice of the surrounding file — this codebase documents reasoning and non-obvious constraints, not mechanics.
- DTOs cross the API boundary; entities never do.
- Mutations send the `Version` (xmin) for optimistic concurrency; a 409 means re-fetch and merge.
- New endpoints inherit the fail-closed fallback authorization policy — an action without `[Authorize]` requires a provisioned Solo user rather than being public. A plain `[Authorize]` means the same: a valid Google sign-in alone is not enough, since anyone can get one from the sign-in page.
- Migrations apply automatically on startup, gated on `Database__AutoMigrate`.
