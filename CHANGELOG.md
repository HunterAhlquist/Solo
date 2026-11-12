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
- A bell in the app bar collects your notifications: when someone assigns you to a task or epic, and when a task of yours is due, two weeks before its deadline and again one week before, at 9:00 your time. Clicking one opens the task.
- With Solo open, a new notification also shows as a notification from your browser or the desktop app once you allow it. Choose which events notify you under **Notifications** in the account menu.
- Email: a super admin sets up SMTP under **Email** in Admin settings, with a test email before saving. The password is encrypted at rest, or comes from `Email__Smtp__Password`. Email goes through a queue and is retried for about 2.5 hours before it shows as failed. Emails use the instance theme's colors and fonts, in light or dark to match the mail app.
- Inviting someone to a project emails them a link to the instance, when email is on. Pending members get a resend button.
- Each person gets an email digest: daily or weekly at the hour they pick, or as it happens, batched every 15 minutes. It lists overdue work, work due within 2 weeks with the earliest deadline first, and work recently assigned to them. Mail apps can unsubscribe in one click. People can now pick their time zone in **Notifications**.
- The web client no longer loads `js/dev-desktop.js`, a local testing shim that was committed by mistake. It did nothing unless a browser had opted in through local storage.
- Extensions can respond to an Apple Pencil double-tap once the iPad app sends one. A browser never sends it, so nothing changes there.
- The iPad and iPhone app can sign in with Google: it opens the instance's sign-in page in the system's sign-in sheet, like the desktop app does in the browser, and returns with a Solo session.
- In the desktop, iPad and iPhone apps, the profile menu has an Instances item, and the sign-in page a "Use another instance" link, which open the app's list of instances.
- The iPad and iPhone app loads its instance through a local origin on the device, as the desktop app does, so keeping a vault offline works in the app (it needs service workers, which iOS only gives an app's own fixed domains). Each instance keeps its own origin, so its sign-in stays its own; the first launch after updating asks you to sign in once more. When the instance can't be reached, the app shows a page saying so, under a pixel-art face with X eyes, with Try again and Instances. After airplane mode, a Wi-Fi change or returning to the app, it drops connections left over from the old network instead of waiting on them.
- The iPad and iPhone app reconnects after the device is locked and unlocked, instead of staying disconnected until it is closed and reopened. Live updates come back within seconds of unlocking, or of the page coming back into view anywhere, rather than at the next of SignalR's retries, which a short lock could push out to half a minute.
- Notifications have a short title, such as "Alex assigned you a task" or "GAME-SP-12 is due in 1 week", and a line under it with more, so a phone's banner no longer cuts them off. The wording comes from a built-in set of lines, some playful, picked at random for an assignment and by the reminder itself for a deadline. The bell, browser and desktop notifications and the email digest show the same text, and notifications you already have keep theirs. The digest's plain-text version puts that line after the title, with the link on its own line.
- When your session expires, the dialog asking you to sign in again hides the page behind it and can't be dismissed. It offers Sign out besides signing in, so a sign-in that keeps failing never leaves you stuck, and in the desktop, iPad and iPhone apps an Open instances button for switching to another instance.
- Opening a board or a page while live updates are reconnecting no longer ends in "Something went wrong. Reload the page to continue." The page opens without live updates, which join it as soon as the connection is back, and opening a board starts that reconnect at once. It happened after the iPad was locked, and anywhere the connection dropped in the middle of a call.
- Live updates keep trying to reconnect after a long outage instead of stopping after a few attempts, so a page left open recovers on its own when the instance comes back.
- On iPhone and iPad, in Safari and in the app, the page now reaches under the status bar and the home indicator and keeps its own content clear of them: the app bar's colour runs up behind the status bar, a drawer's scrim covers it too, and the nav drawer's last row sits above the home indicator instead of under it. The page's theme color follows the app bar, which Safari tints its bars with and the app picks its status bar text by.
- On iPhone and iPad, a full-height page such as a design doc now runs down to the bottom of the screen, under the home indicator, instead of stopping short of it with a strip of background below.
- A preview server checks its own preview release for updates instead of the newest stable one, so Admin settings offers the extension versions built with that preview. Stable instances are unchanged, and `Updates__ManifestUrl` still overrides both.
- A project's owners can create, close, reopen and delete its boards, not only a super admin. Add board appears in the sidebar for each project you own.
- Admin settings → Extensions can switch an extension to another update feed its package offers, such as a beta channel, from an "Updates from" picker. It appears only for a package that lists more than one feed, and switching back to Default keeps the installed version until Default has a newer one.
- Sign-in with Microsoft Entra ID or another OIDC provider works on the web again. The content security policy only allowed Google, so the browser blocked the provider's discovery document and the sign-in ended in "Network Error"; it now allows the endpoints the instance's own provider lists in its discovery document, Entra ID's userinfo endpoint on graph.microsoft.com included, and lets silent renewal frame its authorization endpoint and come back to `/authentication/login-callback`, the one page the app may frame, and only from its own origin.
- The desktop, iPad and iPhone apps sign in with whatever provider the instance uses, not only Google. The app sign-in page (`/desktop-auth`) runs the authorization code flow with PKCE against a Microsoft or generic provider, with no client secret. Register `https://<instance>/desktop-auth` as a redirect URI of the provider's client: for Entra ID, under Single-page application.
- A fourth built-in theme, **Glass**: frosted, floating navigation over a field of pixel icons, with the system font and capsule buttons, built on the fundamentals of Apple's Liquid Glass. In a browser the app bar and the sidebar float as glass panes and the board scrolls under them; in the desktop app's native macOS glass mode it keeps its background art, and the shell's glass panes frost it. It follows Reduce Transparency and Increase Contrast.

### Desktop
- Notifications from your instance show as system notifications while the app is open.
- The app no longer bundles a client: it proxies everything, the client included, to each instance, so it always runs the client its instance serves and an extension's pages always match their server. Each instance keeps its own local address, storage and session. When an instance can't be reached and the client's offline mode hasn't cached it, the window shows a "Can't reach the instance" page, under a pixel-art face with X eyes, with Try again and Instances. Installers are smaller, and a client change no longer needs a desktop release.
- Instances in the profile menu and on the sign-in page opens the Settings window, where the instances are listed.
- An instance now opens even when another program on the computer is using what the app needs for it, instead of stopping with a message to edit settings.json and restart. When that happens, it may ask you to sign in again.
- With no instance yet, the app opens on an "Add your first instance" page, with a link to the guide for setting one up, and nothing else until one is connected. The iPad and iPhone app opens on the same setup.
- Every instance signs in through its sign-in page in the browser or the macOS sign-in sheet, whatever its provider, so a Microsoft or generic instance signs in as a Google one does. Settings no longer lists a per-app OIDC redirect URI: the instance's `/desktop-auth` address replaces it, for the desktop and mobile apps alike.

### SDK
- Contract 1.16.0: the notification and email settings DTOs, `NotificationKind`, `DigestFrequency`, `SmtpSecurity`, `AuthConfigDto.EmailEnabled` and the core user topic `BoardHubConstants.NotificationsTopic`, used by core's own client and API.
- Contract 1.17.0: `ISoloDeviceActions` delivers controls on the device itself as named actions, starting with `DeviceActions.StylusToolSwap` (`"action_stylus-tool-swap"`), the iPad app's Apple Pencil double-tap.
- Contract 1.17.0: `extension.json` accepts `updateFeeds`, other release manifests (`id`, `label`, `url`) an administrator can switch the extension's updates to. `updates` stays the default and is what older hosts read.

### Extensions

#### Design Docs (1.6.5)
- Rebuilt on contract 1.17.0.
- On iPad, tapping the text you're editing in a sketch with your finger brings up the keyboard, and writing on it with Apple Pencil puts the keyboard away so Scribble has the screen. With a box opened by the Pencil, the keyboard used to be hard to bring up. With a hardware keyboard attached, iPadOS keeps the on-screen keyboard hidden either way.
- A design doc's heading and its tree of notes are translucent in every theme, in the browser and the iPad and iPhone app as on the desktop, so a theme's background shows through them; the note itself stays solid for reading. On a narrow screen the tree opens as a frosted panel over the note, thin enough that the note shows through it blurred. Under a floating app bar the heading fades in over a short ramp at its top instead of starting on a hard edge. The page used to be opaque everywhere but the desktop's glass mode.
- The offline sync menu counts the notes, sketches and other items waiting to sync instead of every edit: a sketch counts once however many strokes it has, as a note does however much was typed, so a few offline paragraphs and a drawing read "2 items waiting to sync" rather than hundreds of changes.
- A project's owners can create, rename, close, reopen and delete its design docs, not only a super admin.
- In the iPad app, double-tapping Apple Pencil while sketching switches to the eraser and back to the tool you had before. It follows the Pencil setting in iPadOS: it works when that setting is to switch to the eraser or the previous tool. Needs Solo with contract 1.17.
- In the sketch editor, a two-finger tap undoes and a three-finger tap redoes. A tap has to be quick and still, so pinching and panning work as before, and the finger that lands first never leaves a stray mark.
- The full-screen sketch and image viewer keep their controls clear of the status bar and home indicator on iPhone and iPad.

#### MCP Server (1.6.6)
- Rebuilt on contract 1.17.0.

#### Roadmaps (1.10.4)
- Rebuilt on contract 1.17.0.
- A project's owners can create and delete its roadmaps, not only a super admin.

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
