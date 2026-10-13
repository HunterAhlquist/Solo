# Solo extension templates

Project templates for extensions to [Solo](https://github.com/HunterAhlquist/Solo), the self-hosted project management app for small game-dev teams. An extension adds API endpoints, its own data, hooks on core writes, and pages to a Solo instance without modifying it.

## Install

```bash
dotnet new install HunterAhlquist.Solo.Templates
```

Visual Studio and Rider read the templates that `dotnet new` has installed, so **Solo Extension** then appears in their new-project dialogs, with the options below as fields. Restart the IDE if it was open.

To update the templates later, run `dotnet new update`.

## Create an extension

In Visual Studio, select **Create a new project** and search for "Solo". In Rider, select **New Solution** and find **Solo Extension** in the list of templates.

From the command line:

```bash
dotnet new solo-extension -n Acme.Timesheets --database
```

| Option | Command line | Default | What it does |
|---|---|---|---|
| Extension id | `--extension-id` | From the project name | The route segment (`/api/ext/<id>/`) and the database schema (`ext_<id>`). Lowercase letters, digits and hyphens. |
| Display name | `--display-name` | The project name | The name that Admin settings and the sidebar show. |
| Client page | `--client` | `true` | Adds the client half: a Razor class library with one page, and a sidebar entry that opens it. |
| Sidebar entry | `--nav-scope` | `instance` | `instance` puts one entry in the sidebar footer. `project` puts one under each project. |
| Database schema | `--database` | `false` | Adds a `DbContext` in the extension's own Postgres schema, a first migration, and endpoints that use it. |
| Operation hook | `--hook` | `false` | Adds a hook that runs before a core write and can reject it. |
| Event handler | `--events` | `false` | Adds a handler that runs after a core change is saved. |

## What you get

- A server project that references `HunterAhlquist.Solo.Extensibility`, with the entry point, one endpoint and the manifest (`extension.json`).
- A client project that references `HunterAhlquist.Solo.Shared`, unless you turned the client page off.
- `pack.proj`, which builds the installable package: `dotnet build pack.proj` writes `artifacts/<id>-<version>.zip`.
- A README with the build, install and development steps for the extension.

## Versions

The major and minor version of this package are the version of the extension contract that the generated projects compile against. Version `1.9.x` generates projects for contract `1.9`.

## Documentation

- [Extension development](https://hunterahlquist.github.io/Solo/extensions/): the full guide.
- [samples/HelloExtension](https://github.com/HunterAhlquist/Solo/tree/main/samples/HelloExtension): the smallest extension, written by hand.
