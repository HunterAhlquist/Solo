# Solo Extension Name

An extension for [Solo](https://github.com/HunterAhlquist/Solo). It builds against the published contract packages, and the instance it is installed on supplies them at runtime.

| | |
|---|---|
| `SoloExtension-CSharp/` | The server half. One endpoint, `GET /api/ext/solo-extension-id/greeting`, that greets the signed-in user. |
<!--#if (Client) -->
| `SoloExtension-CSharp.Client/` | The client half. One page, which calls that endpoint. |
<!--#endif -->
| `SoloExtension-CSharp/extension.json` | The manifest: the id, the version, and what the package contains. |
| `SoloExtension-CSharp/Contracts/` | The API's request and response records. |
| `pack.proj` | Builds the package. |

## Build the package

```bash
dotnet build pack.proj
```

This writes two things to `artifacts/`:

- `solo-extension-id-<version>.zip`, the package to install.
- `solo-extension-id/`, the same package as a folder, for the development loop.

The version comes from `extension.json`. Change it there for each release.

## Install it

Copy the `.zip` file into the instance's extensions directory (`Extensions__Directory`; `/data/extensions` in the deployment template) and restart the instance. Solo reads packages only at startup.

**Admin settings → Extensions** then lists the extension as loaded, or gives the reason it did not load.

## The development loop

With a checkout of the Solo repository, load the folder instead of the `.zip` file:

```bash
dotnet build pack.proj
Extensions__DevelopmentDirectories__0="$PWD/artifacts/solo-extension-id" dotnet run --project /path/to/Solo/src/Solo.Api
```

After each change, build the package again and restart Solo.

<!--#if (Database) -->
## Data

`Data/` holds a `DbContext` in the extension's own Postgres schema, `ext_solo_extension_id`, with one example entity, `Note`. `NotesController` reads and writes it at `/api/ext/solo-extension-id/projects/{projectId}/notes`. The instance applies the migrations in `Migrations/` when it starts.

When you replace `Note` with your own entities, replace the first migration too. Delete the `Migrations/` folder, then:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial --project SoloExtension-CSharp
```

Once a version of the extension has been installed anywhere, add a new migration for each change instead.

Two rules keep the extension removable:

- Do not add a foreign key into core's tables. Keep the id of a core row as a plain `Guid`.
- Do not use core's `DbContext`. Read core data through `ISoloQuery`, which returns only what the caller can see.

<!--#endif -->
<!--#if (OperationHook) -->
## The operation hook

`TaskStatusHook` runs before core changes the status of a task, and can refuse the change. A hook runs only for the operations that `extension.json` lists in `hooks`.

<!--#endif -->
<!--#if (EventHandler) -->
## The event handler

`TaskEventsHandler` runs after a change to a task is saved. It cannot refuse the change, and delivery is best-effort.

<!--#endif -->
## Things to know

- The host puts every route under `/api/ext/solo-extension-id/`.
- A project policy (`SoloPolicies.ProjectRead` and the others) needs a project, board, epic or task id in the route. On a route with none, use a plain `[Authorize]`.
- `soloApiVersion` and `soloClientVersion` in the manifest are the lowest contract versions the code needs. Raise them when you use something newer.
<!--#if (Client) -->
- The client project cannot reference `HunterAhlquist.Solo.Extensibility`. It does not restore for the browser.
- Keep the `MudBlazor` and `Microsoft.AspNetCore.Components.Web` versions equal to the ones the instance runs.
<!--#endif -->

## Next

[Extension development](https://hunterahlquist.github.io/Solo/extensions/) is the full guide: the manifest, the server and client halves, hooks and events, and troubleshooting.
