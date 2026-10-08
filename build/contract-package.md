# Solo extension contract

[Solo](https://github.com/HunterAhlquist/Solo) is self-hosted project management for small game-dev teams. Extensions add API endpoints, their own data, hooks on core writes, and client pages to a Solo instance without modifying it.

An extension is two projects, and each references one of these packages:

| Half | SDK | Package | Also needs |
|---|---|---|---|
| Server | `Microsoft.NET.Sdk` | `HunterAhlquist.Solo.Extensibility` | — |
| Client | `Microsoft.NET.Sdk.Razor` | `HunterAhlquist.Solo.Shared` | `Microsoft.AspNetCore.Components.Web`, `MudBlazor` |

The client half can't reference `HunterAhlquist.Solo.Extensibility`: it carries ASP.NET Core and EF Core, and neither resolves for `browser-wasm`.

Both are compile-time references. The instance supplies its own copies at runtime, so leave `Solo.Extensibility.dll`, `Solo.Shared.dll` and MudBlazor out of your extension package.

## Versions

The package version is the extension contract version. Compiling against a newer package than your extension needs is fine: declare the lowest contract version whose features you use in `extension.json`, as `soloApiVersion` for the server half and `soloClientVersion` for the client half. The instance refuses a package outside its range and says why.

## Getting started

- [Writing a Solo extension](https://github.com/HunterAhlquist/Solo/blob/main/docs/writing-extensions.md) — the authoring guide.
- [samples/HelloExtension](https://github.com/HunterAhlquist/Solo/tree/main/samples/HelloExtension) — a minimal extension built from these packages alone.
- [src/Solo.Mcp](https://github.com/HunterAhlquist/Solo/tree/main/src/Solo.Mcp) — a complete one, with its own schema, migrations, authentication scheme and client page.
