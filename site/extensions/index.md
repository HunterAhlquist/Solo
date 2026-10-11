---
title: Extension development
nav_title: Overview
lede: An extension is a package that adds endpoints, data, hooks and pages to a Solo instance. It does this without a change to Solo itself.
---

## What an extension can add

An extension can combine these parts.

| Capability | What you write | Page |
|---|---|---|
| API endpoints | MVC controllers. Solo serves them below `/api/ext/<id>/`. | [Server half](server.md#endpoints) |
| Data | Your own `DbContext`, in your own PostgreSQL schema. | [Server half](server.md#your-own-data) |
| Fields on core records | Values in a JSON field of a task, an epic or a board. | [Server half](server.md#fields-on-core-records) |
| Hooks | Code that runs before a core write and can refuse or change it. | [Hooks and events](hooks-and-events.md#hooks) |
| Events | Code that runs after a core change. | [Hooks and events](hooks-and-events.md#events) |
| Live updates | Messages from your server code to your pages. | [Hooks and events](hooks-and-events.md#live-updates) |
| Background work | Scheduled work that acts as a user who permitted it. | [Hooks and events](hooks-and-events.md#background-work) |
| Pages | Razor components with `@page` routes, and entries in the sidebar. | [Client half](client.md#pages-and-navigation) |
| Parts of core pages | Components that Solo draws in the sidebar, on cards, in the task drawer and in other places. | [Client half](client.md#contribute-to-core-pages) |

## The two halves

An extension has a server half, a client half, or the two.

| Half | Project type | Contract package | Runs in |
|---|---|---|---|
| Server | Class library (`Microsoft.NET.Sdk`) | `HunterAhlquist.Solo.Extensibility` | The Solo process |
| Client | Razor class library (`Microsoft.NET.Sdk.Razor`) | `HunterAhlquist.Solo.Shared` | The browser of each person |

The client half also references `Microsoft.AspNetCore.Components.Web` and `MudBlazor`, at the versions that the instance runs.

The client half cannot reference `HunterAhlquist.Solo.Extensibility`. That package depends on ASP.NET Core and EF Core, and they do not build for the browser.

## The rule: change no core project

An extension is its own project. It references the contract packages and no other part of Solo.

If you edit `Solo.Api`, `Solo.Data`, `Solo.Client`, `Solo.Shared` or `Solo.Extensions`, you are changing Solo. You are not writing an extension.

The test is removal. When an administrator removes the package, the instance loses the feature of the extension and no other feature.

If the contract does not let you do what you need, the contract has a gap. Do not use the database or the core assemblies directly to get past it.

## Trust

> An extension is code that an administrator deploys. The server half runs in the Solo process and gets the database connection string. The client half runs in the same browser context as the session token. Solo has no sandbox. The manifest is a description, not a security boundary.
{: .warning}

Write an extension with this in mind:

- Use [`ISoloQuery`](server.md#read-core-data) to read core data. It applies the project access of the caller.
- Write core data through the Solo API, not through the database. The API applies authorization, hooks, the audit log and live updates.
- Put no secret in the `content/` folder. Solo serves that folder without sign-in.

## The contract and its version

The two contract packages have one version: the **contract version**. This documentation describes contract `1.8`.

- Compile against the newest package.
- In the manifest, declare the lowest contract version that your code needs. See [Version ranges](manifest.md#version-ranges).

An instance refuses a package that needs a newer contract than it has, and gives the reason. [Reference](reference.md#contract-versions) lists what each version added.

### Get the contract packages

The packages are `HunterAhlquist.Solo.Extensibility` and `HunterAhlquist.Solo.Shared`.

If nuget.org does not have them, build them from the Solo repository:

```bash
git clone https://github.com/HunterAhlquist/Solo.git
cd Solo
build/pack-contract.sh
```

The script writes the packages to `artifacts/nuget`. Add that folder as a package source:

```bash
dotnet nuget add source "$PWD/artifacts/nuget" --name solo-contract
```

## Examples to read

| Example | What it shows |
|---|---|
| [`samples/HelloExtension`](https://github.com/HunterAhlquist/Solo/tree/main/samples/HelloExtension) | The smallest extension with the two halves. [Your first extension](first-extension.md) builds it. |
| [`extensions/Mcp`](https://github.com/HunterAhlquist/Solo/tree/main/extensions/Mcp) | A full extension: its own schema and migrations, endpoints, an authentication scheme, a page, static content and an audit contributor. |
| [`extensions/Roadmap`](https://github.com/HunterAhlquist/Solo/tree/main/extensions/Roadmap) | An extension that uses most client contributions: sidebar sections, the task drawer, the task picker, menus, live updates and a settings panel. |

## In this section

1. [Your first extension](first-extension.md): build, package and install a small extension.
2. [Manifest](manifest.md): each field of `extension.json`.
3. [Server half](server.md): endpoints, access to core data, and your own data.
4. [Hooks and events](hooks-and-events.md): hooks, events, live updates and background work.
5. [Client half](client.md): pages, the API client, and contributions to core pages.
6. [Package and install](package.md): the package layout and the development loop.
7. [Reference](reference.md): contract versions, operations, policies and limits.
8. [Troubleshooting](troubleshooting.md): symptoms and their causes.
