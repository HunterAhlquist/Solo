---
title: Manifest
lede: The manifest is the file extension.json at the root of a package. It tells Solo what the package contains and what it needs.
---

## Example

```jsonc
{
  "id": "roadmap",
  "name": "Roadmap",
  "version": "1.0.0",
  "soloApiVersion": ">=1.4 <2.0",
  "soloClientVersion": ">=1.6 <2.0",
  "server": "server/Roadmap.dll",
  "client": "client/Roadmap.Client.dll",
  "schema": "ext_roadmap",
  "nav": [
    { "route": "/roadmap/{projectAlias}", "label": "Roadmap",
      "icon": "Timeline", "requires": "Read" }
  ],
  "hooks": ["task.setStatus"],
  "failOpen": false
}
```

## Fields

| Field | Required | Content |
|---|---|---|
| `id` | Yes | The ID of the extension. 1 to 64 characters: `a` to `z`, `0` to `9` and `-`. The first character is a letter or a digit. |
| `name` | No | The name that the Admin settings show. |
| `version` | Yes | The version of your package. |
| `soloApiVersion` | With `server` | The contract versions that the server half accepts. See [Version ranges](#version-ranges). |
| `soloClientVersion` | With `client` | The contract versions that the client half accepts. |
| `server` | One of the two | The path of the server assembly in the package. |
| `client` | One of the two | The path of the client assembly in the package. |
| `schema` | With data | The PostgreSQL schema of the extension. See [Schema](#schema). |
| `nav` | No | Entries for the sidebar. See [Navigation entries](#navigation-entries). |
| `hooks` | With hooks | The operations that your hooks run on. See [Hooks](#hooks). |
| `failOpen` | No | `true` lets a request continue when a hook fails. The default is `false`. |

A package must have a `server` assembly, a `client` assembly, or the two.

## The ID

The ID identifies the extension in three places:

- The route of its endpoints: `/api/ext/<id>/`.
- The route of its static content: `/api/extensions/<id>/content/`.
- The name of its database schema: `ext_<id>`.

Do not change the ID after you release an extension. With a new ID, the extension loses its data.

## Version ranges

A version range has a lower limit, an upper limit, or the two, with a space between them:

```
>=1.3 <2.0
```

| Part | Meaning |
|---|---|
| `>=1.3` | The contract version is 1.3 or higher. |
| `<2.0` | The contract version is lower than 2.0. |

You can write a version as `1`, `1.3` or `1.3.0`.

Set the lower limit to the lowest contract version that has each feature you use. [Reference](reference.md#contract-versions) lists the features of each version. Set the upper limit to the next major version.

### Why there are two ranges

Solo checks `soloApiVersion` on the server, at startup. Solo checks `soloClientVersion` in the browser, before it loads the client assembly.

The two checks can give different results. The desktop app contains its own copy of the client, and that copy can be older than the instance. When the client is too old for your `soloClientVersion`, it does not load your client half. Your pages and sidebar entries are then absent, and the server half still runs.

## Schema

Declare `schema` only when the extension has its own database tables.

The value is not your decision. It must be `ext_` and then the ID, with each `-` replaced by `_`:

| ID | Schema |
|---|---|
| `roadmap` | `ext_roadmap` |
| `time-tracker` | `ext_time_tracker` |

Solo refuses a package with a different value. This rule makes sure that the deletion of extension data cannot reach the data of Solo or of a different extension.

## Navigation entries

Each entry in `nav` adds one link to the sidebar.

| Field | Required | Content |
|---|---|---|
| `route` | Yes | The route of a page in your client half. It starts with `/`. |
| `label` | Yes | The text of the link. |
| `icon` | No | The name of an icon. Without it, Solo shows a default icon. |
| `requires` | No | The lowest project access that a person needs to see the link: `Read`, `Write` or `Owner`. The default is `Read`. |

The route sets where the link appears:

- **With `{projectAlias}`.** Solo shows the link below each project, and replaces `{projectAlias}` with the alias of that project. `requires` applies to the role of the person in that project.
- **Without `{projectAlias}`.** Solo shows the link one time, at the bottom of the sidebar.

Solo shows a link only when the client assembly loaded. If the client is too old for the package, the link is absent.

`requires` only hides a link. Your endpoints must check access themselves. See [Authorization](server.md#authorization).

## Hooks

List each operation that your hooks run on:

```json
"hooks": ["task.setStatus", "task.setAssignees"]
```

Solo runs a hook only for the operations in this list. If a hook names an operation that is not in the list, Solo ignores the hook for that operation. Solo refuses a package that lists an unknown operation.

The Admin settings show the list as **Intercepts**, so an administrator can see which extensions act on which changes.

[Reference](reference.md#operations) lists the operations.

### failOpen

By default, a request fails when one of your hooks throws an exception or takes too long. Set `"failOpen": true` only if your hooks give advice and a request can safely continue without them.

## What Solo checks at load

Solo refuses a package, and gives the reason in the Admin settings, when:

- `extension.json` is missing or is not valid JSON.
- `id` or `version` is missing or not valid.
- The manifest names no `server` and no `client` assembly, or names a file that is not in the package.
- `schema` is not the value that the ID requires.
- `hooks` names an unknown operation.
- A `nav` entry has no `label`, or its `route` does not start with `/`.
- A version range is missing for a half that the package has, or is not valid.
- The contract version of the instance is outside `soloApiVersion`.
- The package is too large. See [Limits](reference.md#limits).
