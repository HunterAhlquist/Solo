---
title: Project template
lede: Create the projects of an extension from a template, in Visual Studio, in Rider or from the command line.
---

## What the template is

The template is the NuGet package `HunterAhlquist.Solo.Templates`. It adds **Solo Extension** to `dotnet new`, and to the new-project dialogs of Visual Studio and Rider.

The template creates what [Your first extension](first-extension.md) builds by hand: the two projects, the manifest and a first endpoint. Options add a database schema, a hook and an event handler.

## Install the template

You need the .NET 10 SDK.

```bash
dotnet new install HunterAhlquist.Solo.Templates
```

Visual Studio and Rider show the templates that `dotnet new` has. If the IDE was open, restart it.

To get a newer version of the template later:

```bash
dotnet new update
```

## Create an extension

### In Visual Studio

1. Select **Create a new project**.
2. Search for `Solo`, and select **Solo Extension**.
3. Enter the project name and the location.
4. Set the options on the next page, and select **Create**.

### In Rider

1. Select **New Solution**.
2. Select **Solo Extension** in the list of templates.
3. Enter the solution name, and set the options.
4. Select **Create**.

### From the command line

```bash
dotnet new solo-extension -n Acme.Timesheets --database
```

`dotnet new solo-extension --help` lists the options.

## Options

| Option | Command line | Default | Result |
|---|---|---|---|
| **Extension id** | `--extension-id` | From the project name | The `id` in the manifest. See [The ID](manifest.md#the-id). |
| **Display name** | `--display-name` | The project name | The `name` in the manifest, and the label of the sidebar entry. |
| **Client page** | `--client` | `true` | The client half: one page, and one sidebar entry that opens it. |
| **Sidebar entry** | `--nav-scope` | `instance` | `instance` gives one entry at the bottom of the sidebar. `project` gives one entry below each project. |
| **Database schema** | `--database` | `false` | A `DbContext` in the schema of the extension, a first migration, and endpoints that use them. |
| **Operation hook** | `--hook` | `false` | A hook that runs before a change to the status of a task. |
| **Event handler** | `--events` | `false` | A handler that runs after a change to a task. |

The template makes the id from the project name. The project `Acme.Timesheets` gets the id `acme-timesheets`. The names of the classes come from the last part of the project name: `TimesheetsExtension`, `TimesheetsPage`.

## What the template creates

For the project name `Acme.Timesheets`, with all options on:

```
Acme.Timesheets/                 the server half
├── extension.json               the manifest
├── TimesheetsExtension.cs       the entry point
├── GreetingController.cs        GET /api/ext/acme-timesheets/greeting
├── NotesController.cs           endpoints that use the database schema
├── TaskStatusHook.cs            the hook
├── TaskEventsHandler.cs         the event handler
├── Contracts/                   types that the two halves share
├── Data/                        the DbContext and one entity
└── Migrations/                  the first migration
Acme.Timesheets.Client/          the client half
└── TimesheetsPage.razor         the page
pack.proj                        makes the package
README.md
```

The projects reference the contract packages on nuget.org. The template has the same major and minor version as the contract that the projects compile against.

## Make the package

```bash
dotnet build pack.proj
```

This command builds the projects and writes two things into `artifacts/`:

- `<id>-<version>.zip`, the package to install. See [Install](package.md#install).
- A folder `<id>/` with the same content. Use it for [the development loop](package.md#the-development-loop).

The command reads the id and the version from the manifest. It copies only your own assemblies, and the files in a `content/` folder next to the manifest.

If your extension has a library of its own, add the library to the `Copy` step of its half in `pack.proj`.

## After you change the example entity

The **Database schema** option gives you one example entity, `Note`, and a migration that creates its table.

When you replace `Note` with your own entities, also replace the migration:

1. Delete the `Migrations` folder.
2. Make a new first migration:

   ```bash
   dotnet ef migrations add Initial --project Acme.Timesheets
   ```

After you install a version of the extension on an instance, do not replace the migrations. Add a new migration for each change. [Your own data](server.md#your-own-data) has the rules.

## Next

- Each field of the manifest: [Manifest](manifest.md).
- Endpoints, access to core data, and your own data: [Server half](server.md).
- Pages and contributions to core pages: [Client half](client.md).
