# Changelog

Every change lands under **Latest** as it is made, because the version is not
known until the tag is pushed. Pushing a `v*` tag publishes the Latest section
as the GitHub release's notes, then CI renames the header to the tag and date
and commits it back to `main`. If there is no `## Latest` header, add one at
the top and start the next release's section there.

Sections, in this order and only when they have something in them: **Server**
(the image, which includes the web client), **Desktop**, **SDK** (the contract
packages and templates), **Extensions** (one `####` per first-party extension,
with its version in parentheses).

## Latest

### Desktop
- The update dialog names the installed version as its heading and asks about the new one with Update Now / Not Now buttons. The download-size estimate it showed is gone; the progress window reports the real size as the download runs.

### Extensions

#### Design Docs (1.5.3)
- Block quotes draw an Obsidian-style bar in the theme's primary color, one per nesting level, and hide the `>` marker unless the caret is on that line.
- Wrapped rows of an indented line, a list item or a quoted line hang under the line's text instead of falling back to the left edge.

## v1.19.9 (2026-11-03)

### Server
- Each Your Tasks card names its group and epic above its title, and epic and group changes refresh the dashboards.
- An extension action route with a leading slash stays under `/api/ext/{id}` instead of escaping the extension's namespace.
- The private-range check guarding the avatar proxy and update feeds now also covers 100.64/10 (Tailscale), 0/8, 192.0.0/24, 198.18/15, multicast and class E.

### Desktop
- A progress window opens on Download and shows the update's version, a bar, size, speed and time left, with a Cancel button; Check for Updates… brings it back.
- The local proxy's Host check also guards WebSocket upgrades.

### SDK
- Contract 1.15.0: `AssignedTasksDto` carries the epics the caller's tasks sit under and the groups those epics belong to, as optional trailing members.
- A stable `v*` tag also publishes the contract and templates packages to nuget.org when it is behind the version in the checkout.

### Extensions

#### MCP Server (1.6.4)
- Rebuilt on contract 1.15.0.

#### Roadmaps (1.10.1)
- Rebuilt on contract 1.15.0.

#### Design Docs (1.5.2)
- Rebuilt on contract 1.15.0.
