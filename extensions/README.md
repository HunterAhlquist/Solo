# Extensions

The extensions this repository ships, each its own solution and none of them in
`Solo.slnx`. Deleting a folder here leaves core building and its tests passing;
that is the point.

| Folder | Package | Solution |
|---|---|---|
| [Mcp](Mcp) | `solo-mcp` — the MCP server, and the reference extension | `Solo.Mcp.slnx` |
| [Roadmap](Roadmap) | `solo-roadmap` — timelines | `Solo.Roadmap.slnx` |

Both build the way a third-party extension does: the server half against the
`HunterAhlquist.Solo.Extensibility` package, the client half against
`HunterAhlquist.Solo.Shared`, never a project in `src/`. Until those packages
are on nuget.org they exist only as what `build/pack-contract.sh` writes to
`artifacts/nuget`, which [nuget.config](nuget.config) makes their one source:

```sh
build/pack-contract.sh                      # again after changing the contract
dotnet test extensions/Mcp/Solo.Mcp.slnx
build/pack-mcp-extension.sh                 # → artifacts/solo-mcp-<version>.zip
```

The test projects are the exception to "nothing from `src/`". They load the
package into the real host, so they reference `Solo.Api` and the harnesses
core's own tests use (`tests/Solo.Api.Testing`, `tests/Solo.Client.Testing`).

CI packs the contract, runs both solutions' tests and builds both packages on
every pull request; see `.github/workflows/ci.yml`. Writing a new one:
[docs/writing-extensions.md](../docs/writing-extensions.md).
