---
title: Package and install
lede: Put your assemblies and the manifest in a .zip file, install it on an instance, and use a development directory for a faster loop.
---

## Package layout

```
roadmap.zip
├── extension.json              the manifest, at the root
├── server/
│   └── Roadmap.dll             the server assembly and your own dependencies
├── client/
│   └── Roadmap.Client.dll      the client assembly and your own dependencies
└── content/                    optional static files
    ├── roadmap.css
    └── roadmap.js
```

The paths in the manifest fields `server` and `client` point to the two assemblies.

File and folder names can contain letters, digits, `.`, `_` and `-`.

## What to put in the package

Put only your own files in the package:

- Your server assembly and your client assembly.
- Libraries that only your extension uses.

Do not put these in the package. The instance has its own copies, and it uses only those:

- `Solo.Shared.dll` and `Solo.Extensibility.dll`
- MudBlazor
- ASP.NET Core, EF Core and Npgsql

The build output folder contains all of these files. Do not make the `.zip` file from the full folder. Copy your own files out of it.

## Make the package

```bash
dotnet build Roadmap -c Release
dotnet build Roadmap.Client -c Release

mkdir -p pkg/server pkg/client pkg/content
cp extension.json pkg/
cp Roadmap/bin/Release/net10.0/Roadmap.dll pkg/server/
cp Roadmap.Client/bin/Release/net10.0/Roadmap.Client.dll pkg/client/
cp content/* pkg/content/
(cd pkg && zip -r ../roadmap.zip .)
```

Keep the version in the manifest equal to the version of your two projects. The scripts for the bundled extensions, such as [`build/pack-mcp-extension.sh`](https://github.com/HunterAhlquist/Solo/blob/main/build/pack-mcp-extension.sh), stop when these versions are different.

## Install

1. Copy the `.zip` file into the extensions directory of the instance. The setting `Extensions__Directory` names the directory. The default is `/data/extensions`.
2. Restart the instance.

Solo reads packages only at startup. Solo cannot add controllers, hooks or services after it builds its application. Each installation, update and removal needs a restart.

[Install an extension](../admin/packages.md#install-an-extension) has the commands for Docker Compose.

## The development loop

During development, you do not need a `.zip` file. Solo can load a package from a folder with the same layout.

Set `Extensions__DevelopmentDirectories__0` to the folder. Solo applies the same checks as for a `.zip` file.

With a checkout of the Solo repository:

```bash
dotnet build Roadmap -o ./pkg/server
dotnet build Roadmap.Client -o ./pkg/client
cp extension.json ./pkg/

Extensions__DevelopmentDirectories__0="$PWD/pkg" dotnet run --project /path/to/Solo/src/Solo.Api
```

To load a second folder, also set `Extensions__DevelopmentDirectories__1`.

A build with `-o` puts all dependencies in the folder. Solo ignores the copies of the libraries that it supplies. This layout is acceptable for development. For a release, make a clean package.

After each change, build again and restart Solo.

## Check that the extension loaded

### In the Admin settings

The **Extensions** card of the [Admin settings](../admin/admin-settings.md#extensions) lists each extension with its state.

| State | Meaning | What to do |
|---|---|---|
| loaded | The extension runs. | No action is necessary. |
| disabled | The extension did not load, or a migration failed. | Read the reason in the entry. |
| data present, package missing | The package is gone and the schema is still in the database. | Install the package again, or delete the data. |

A package that Solo cannot read shows as a warning below the list, with the file and the reason.

### With a request

```bash
curl -H "Authorization: Bearer $SOLO_TOKEN" \
  https://solo.example.com/api/ext/roadmap/milestones/$BOARD_ID
```

A disabled extension answers with status `503` and the reason.

### In the database

```sql
\dn                  -- the schema ext_roadmap is in the list
\dt ext_roadmap.*    -- your tables, and your own __EFMigrationsHistory
```

## Test an extension

The bundled extensions have test projects that load the package into the real Solo host. They are the pattern to copy:

- [`extensions/Mcp/Solo.Mcp.Tests`](https://github.com/HunterAhlquist/Solo/tree/main/extensions/Mcp/Solo.Mcp.Tests)
- [`extensions/Roadmap/Solo.Roadmap.Tests`](https://github.com/HunterAhlquist/Solo/tree/main/extensions/Roadmap/Solo.Roadmap.Tests)

These tests reference projects in `src/`, so they need a checkout of the Solo repository. They use Testcontainers, so they also need Docker.

## Release notes for your users

Tell the administrators of your extension:

- The lowest Solo server version that your `soloApiVersion` accepts.
- If a new version needs a newer client, people who use the desktop app must update the app.
- If a new version has a migration, Solo applies it at the next start.
