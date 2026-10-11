---
title: Troubleshooting
lede: Symptoms that extension authors see, with the cause and the correction for each.
---

## Start here

Open the **Extensions** card in the [Admin settings](../admin/admin-settings.md#extensions). It shows the state of each extension, and the reason when an extension did not load. The startup log has the same information.

Remember that Solo reads packages only at startup. After each change to a package, restart the instance.

## The package does not load

| Symptom | Cause | Correction |
|---|---|---|
| The package shows as a warning, with a message about `schema`. | `schema` is not `ext_` and the ID, with `-` replaced by `_`. | Use the value from [Schema](manifest.md#schema). |
| The message says that a version range is required or not valid. | `soloApiVersion` or `soloClientVersion` is missing or has a wrong format. | See [Version ranges](manifest.md#version-ranges). |
| The message says that the contract version is outside the range. | The instance is older than the lower limit of `soloApiVersion`. | Update the instance, or use only features of an older contract. |
| The message names an assembly that is not in the package. | The path in `server` or `client` is wrong. | Compare the manifest with the files in the `.zip` file. |
| The message names an unknown operation. | `hooks` has a name that is not in the [list of operations](reference.md#operations). | Correct the name. |
| The change has no effect. | Solo did not restart. | Restart the instance. |

## The server half

| Symptom | Cause | Correction |
|---|---|---|
| Each endpoint answers `503`. | The extension is disabled. The usual cause is a failed migration. | Read the reason in the Admin settings. |
| An endpoint answers `403` to each request. | The endpoint has a project policy, and its route has no project, board, epic, task or sub-task ID. | Use a plain `[Authorize]` and read data through `ISoloQuery`. |
| The extension sees projects that the caller is not a member of. | The code uses `SoloDbContext`. | Use `ISoloQuery`. |
| The migrations of Solo fail, or the model does not match the snapshot. | The code adds entity types to `SoloDbContext`. | Use your own `DbContext`. See [Your own data](server.md#your-own-data). |
| The hook does not run. | The operation is not in the `hooks` list of the manifest. | Add the operation to the list. |
| A core request fails with an error that names your extension. | A hook threw an exception or took longer than 2 seconds. | Correct the hook. For a hook that only gives advice, see [failOpen](manifest.md#failopen). |
| An event handler does not delete rows after a board move. | A move sends `Deleted` for the old board, and the task still exists. | This is correct behaviour. See [Rules for events](hooks-and-events.md#rules-for-events). |
| `ISoloQuery` returns no data in a hosted service. | Scheduled work has no caller. | Use [`ISoloBackground`](hooks-and-events.md#background-work). |
| Background work cannot connect to the API. | The container cannot connect to its own listen address. | Set `Extensions__ApiBaseUrl`. |

## The client half

| Symptom | Cause | Correction |
|---|---|---|
| The client project does not restore for the browser. | The project references `HunterAhlquist.Solo.Extensibility`. | Reference `HunterAhlquist.Solo.Shared`, `Microsoft.AspNetCore.Components.Web` and `MudBlazor`. |
| The page cannot call the API. | The page injects `HttpClient`. | Inject `ISoloClientApi`. |
| A sidebar entry is missing in the desktop app, but present in a browser. | The client in the desktop app is older than the lower limit of `soloClientVersion`. | Update the desktop app, or lower the limit if your code permits it. |
| A sidebar entry shows one time, not for each project. | The route of the `nav` entry has no `{projectAlias}`. | Add `{projectAlias}` to the route. |
| An address that a person copied starts with `localhost:41321`. | The code uses `Resolve`. | Use `ResolveExternal` for an address that a person copies. |
| The theme disappears while one page is open. | The page puts a stylesheet in `<HeadContent>`. | Put the `<link>` in the markup of the page. See [Add a stylesheet](client.md#add-a-stylesheet). |
| A new page loads an old script. | A proxy or the desktop app kept the old file. | Add `?v=` and the package version to each content address. |
| A click on your control in a task card opens the task drawer. | The click reaches the card. | Add `@onclick:stopPropagation` to the control. |
| The browser menu opens on top of your menu. | The element does not stop the default action. | Add `@oncontextmenu:preventDefault="true"`. |

## The first start

At the first start of an extension with data, the log shows a `fail:` line that contains `__EFMigrationsHistory`. This line is not a failure. See [The first start of an extension with data](../admin/packages.md#the-first-start-of-an-extension-with-data).
