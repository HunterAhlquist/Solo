# Hello — a minimal Solo extension

The smallest extension with both halves, built from the published contract packages and nothing else from the Solo repository. Read it to see every part; to start an extension of your own, `dotnet new install HunterAhlquist.Solo.Templates` generates the same thing under your names, from the command line, Visual Studio or Rider.

| | |
|---|---|
| `Hello/` | Server half. References `HunterAhlquist.Solo.Extensibility`. One endpoint, `GET /api/ext/hello/greeting`, that greets the signed-in user. |
| `Hello.Client/` | Client half. References `HunterAhlquist.Solo.Shared`, Components.Web and MudBlazor. One page, `/hello`, that calls the endpoint. |
| `Hello/Contracts/` | The response record, compiled into both halves. |
| `extension.json` | The manifest, at the package root. |

## Build

```bash
dotnet build Hello.slnx -c Release
```

## Package

A package is a zip with the manifest at its root and each half's assembly in its own folder:

```bash
mkdir -p pkg/server pkg/client
cp extension.json pkg/
cp Hello/bin/Release/net10.0/Hello.dll pkg/server/
cp Hello.Client/bin/Release/net10.0/Hello.Client.dll pkg/client/
(cd pkg && zip -r ../hello.zip .)
```

Ship only your own assemblies, plus any private dependencies of your own. The instance supplies `Solo.Shared`, `Solo.Extensibility`, MudBlazor, ASP.NET Core and EF Core at runtime.

Drop `hello.zip` into the instance's extensions directory (`/data/extensions` in the deployment template) and restart. **Hello** appears in the sidebar footer, and Admin settings lists the extension as loaded.

## Next

[Writing a Solo extension](../../docs/writing-extensions.md) covers data in your own schema, hooks on core writes, events, and the client seams. `extensions/Mcp` is a complete extension that uses most of them.

Inside the Solo repository, `build/pack-contract.sh` builds this sample against freshly packed contract packages, which keeps it honest. Bump its `HunterAhlquist.Solo.*` versions whenever the contract version moves.
