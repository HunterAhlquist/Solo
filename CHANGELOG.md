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
- Extensions can respond to an Apple Pencil double-tap once the iPad app sends one. A browser never sends it, so nothing changes there.
- The iPad and iPhone app can sign in with Google: it opens the instance's sign-in page in the system's sign-in sheet, like the desktop app does in the browser, and returns with a Solo session.
- In the desktop, iPad and iPhone apps, the profile menu has an Instances item, and the sign-in page a "Use another instance" link, which open the app's list of instances.
- On iPhone and iPad, in Safari and in the app, the page now reaches under the status bar and the home indicator and keeps its own content clear of them: the app bar's colour runs up behind the status bar, a drawer's scrim covers it too, and the nav drawer's last row sits above the home indicator instead of under it. The page's theme color follows the app bar, which Safari tints its bars with and the app picks its status bar text by.

### Desktop
- Instances in the profile menu and on the sign-in page opens the Settings window, where the instances are listed.

### SDK
- Contract 1.16.0: `ISoloDeviceActions` delivers controls on the device itself as named actions, starting with `DeviceActions.StylusToolSwap` (`"action_stylus-tool-swap"`), the iPad app's Apple Pencil double-tap.

### Extensions

#### Design Docs (1.6.0)
- In the iPad app, double-tapping Apple Pencil while sketching switches to the eraser and back to the tool you had before. It follows the Pencil setting in iPadOS: it works when that setting is to switch to the eraser or the previous tool. Needs Solo with contract 1.16.
- In the sketch editor, a two-finger tap undoes and a three-finger tap redoes. A tap has to be quick and still, so pinching and panning work as before, and the finger that lands first never leaves a stray mark.
- The full-screen sketch and image viewer keep their controls clear of the status bar and home indicator on iPhone and iPad.

#### MCP Server (1.6.5)
- Rebuilt on contract 1.16.0.

#### Roadmaps (1.10.2)
- Rebuilt on contract 1.16.0.

## v1.20.1 (2026-11-05)

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
