# Changelog

Every change lands under **Latest** as it is made, because the version is not
known until the tag is pushed. Pushing a `v*` tag publishes the Latest section
as the GitHub release's notes. Before adding an entry, run
`build/changelog.sh sync`: when a newer tag has shipped, it renames Latest to
that tag and date and starts a new Latest on top.

Sections, in this order and only when they have something in them: **Server**
(the image, which includes the web client), **Desktop**, **SDK** (the contract
packages and templates), **Extensions** (one `####` per first-party extension,
with its version in parentheses).

## Latest

### Server
- Signing in with Google in a browser now lasts like the desktop app's sign-in: until you sign out, or until 30 days pass without using Solo (`Auth__SessionDays`). Before, it ran out after an hour, and coming back meant an empty page for up to 15 seconds before the sign-in screen, longest on phones. A browser whose last sign-in is more than an hour old signs in once more after this update.
- The browser's sign-in is kept in a cookie that scripts on the page can't read, and the Google sign-in token is no longer stored in the browser. Browsers now appear in **Signed-in devices** next to desktop apps, and can be signed out from there.

## v1.20.0 (2026-11-04)

### Desktop
- The update dialog names the installed version as its heading and asks about the new one with Update Now / Not Now buttons. The download-size estimate it showed is gone; the progress window reports the real size as the download runs.

### Extensions

#### Design Docs (1.5.3)
- Block quotes draw an Obsidian-style bar in the theme's primary color, one per nesting level, and hide the `>` marker unless the caret is on that line.
- Wrapped rows of an indented line, a list item or a quoted line hang under the line's text instead of falling back to the left edge.
- On a phone, opening a design doc shows the note instead of the note tree; the tree only opens by itself when there is no note to show.
- On a phone, the note tree fills the screen's width, and on narrow screens it covers the insert palette instead of sitting under it.
- On a phone, the insert palette is slimmer and tucks behind the screen's left edge with a primary-colored tab on the current line. Tapping the tab closes the keyboard and pops the palette out over the text; tapping back into the note tucks it away.
- Note text on touch screens is at least 16px, so iOS no longer zooms in when the keyboard opens.
- On a phone, the header's buttons share their own evenly spaced row under the vault name, with less padding around the header.
- The note tree's Note and Folder buttons are spaced evenly.
- The download button is disabled once a vault is downloaded, and keeps showing the downloaded icon in its normal color.

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
