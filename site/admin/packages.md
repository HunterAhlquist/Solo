---
title: Themes and extensions
lede: Install, update and remove theme packages and extension packages on your instance.
---

## Before you install a package

A theme package and an extension package are `.zip` files. You install one by copying it into a directory of the instance. Solo has no upload button.

> An extension is code that runs in Solo. The server part runs in the Solo process with the database connection. The client part runs in the browser with the session of each person. Solo has no sandbox for extensions. Install an extension only if you trust it as much as the Solo image.
{: .danger}

A theme contains no code, but its stylesheet can change all parts of all pages. Install a theme only from a source that you trust.

## Where the packages go

| Package | Directory in the container | Setting |
|---|---|---|
| Theme | `/data/themes` | `Themes__Directory` |
| Extension | `/data/extensions` | `Extensions__Directory` |

With the Compose file from [Install](install.md), the two directories are named volumes. To copy a file into a named volume, use `docker compose cp`:

```bash
docker compose cp my-theme.zip solo:/data/themes/
```

With a [host path](configuration.md#host-paths), copy the file into the host directory.

## Install a theme

1. Copy the `.zip` file into the themes directory.
2. Open the [Admin settings](admin-settings.md#theme).
3. Select the rescan button in the **Theme** card.
4. Select the new theme in the list.
5. Load the page again.

No restart is necessary.

If a package does not load, the **Theme** card lists the file and the reason. Solo keeps the themes that it loaded before, so a damaged file cannot stop the instance.

To write a theme, see [Theme development](../themes/index.md).

### Remove a theme

1. Delete the `.zip` file from the themes directory.
2. Select the rescan button in the **Theme** card.

If you remove the selected theme, Solo uses the default theme.

## Install an extension

Two extensions come with each server release: `solo-mcp` and `solo-roadmap`. Download their `.zip` files from the [releases page](https://github.com/HunterAhlquist/Solo/releases). Use a release with a name that starts with `server-v`.

1. Copy the `.zip` file into the extensions directory:

   ```bash
   docker compose cp solo-roadmap-1.7.0.zip solo:/data/extensions/
   ```

2. Restart the container:

   ```bash
   docker compose restart solo
   ```

3. Open the [Admin settings](admin-settings.md#extensions).
4. Make sure that the extension has the state **loaded**.

Solo reads extension packages only at startup. Each installation, update or removal needs a restart.

If an extension does not load, Solo still starts. The **Extensions** card gives the reason.

### The first start of an extension with data

An extension that stores data gets its own schema in the database, with the name `ext_<id>`. Solo creates the tables at the first start.

At that first start, the log shows a line that starts with `fail:` and contains `__EFMigrationsHistory`. This line is not a failure. The database library looks for a table that does not exist yet.

These two lines from Solo tell you the result:

```
Extension '<id>': applying migrations to schema ext_<id>.
Extension '<id>' migrated into schema ext_<id>.
```

If the migration of an extension fails, Solo disables only that extension. Its endpoints answer with status `503` and the reason.

### Update an extension

1. Delete the old `.zip` file from the extensions directory.
2. Copy the new `.zip` file into the directory.
3. Restart the container.

People who use the [desktop app](../guide/desktop.md) can need a newer app for a newer extension. If an extension page is missing in the desktop app, install the newest desktop release.

### Remove an extension

1. Delete the `.zip` file from the extensions directory.
2. Restart the container.

Solo keeps the data of the extension. The **Extensions** card then shows the extension with the state **data present, package missing**. If you install the package again, the extension uses the data that Solo kept.

To delete the data permanently, see [Delete the data of an extension](admin-settings.md#delete-the-data-of-an-extension).

## Write your own

- [Theme development](../themes/index.md)
- [Extension development](../extensions/index.md)
