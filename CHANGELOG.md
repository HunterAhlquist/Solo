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
- A bell in the app bar collects your notifications: when someone assigns you to a task or epic, and when a task of yours is due, two weeks before its deadline and again one week before, at 9:00 your time. Clicking one opens the task, and **Clear all** empties the bell.
- With Solo open, a new notification also shows as a notification from your browser or the desktop app once you allow it. Choose which events notify you under **Notifications** in the account menu.
- Email: a super admin sets up SMTP under **Email** in Admin settings, with a test email before saving. The password is encrypted at rest, or comes from `Email__Smtp__Password`, which is sent only to the server that `Email__Smtp__Host` names and is not used without it. A stored password is forgotten when the server changes or the connection is set to unencrypted, and Solo signs in to an SMTP server without encryption only when that server is on `localhost`. Email goes through a queue and is retried for about 2.5 hours before it shows as failed. Emails use the instance theme's colors and fonts, in light or dark to match the mail app.
- Inviting someone to a project emails them a link to the instance, when email is on. Pending members get a resend button. One person can send 60 invitations an hour (`RateLimiting__Invites__PermitLimit`).
- Each person gets an email digest: daily or weekly at the hour they pick, or as it happens, batched every 15 minutes. It lists overdue work, work due within 2 weeks with the earliest deadline first, and work recently assigned to them. Mail apps can unsubscribe in one click. People can now pick their time zone in **Notifications**.
- The web client no longer loads `js/dev-desktop.js`, a local testing shim that was committed by mistake. It did nothing unless a browser had opted in through local storage.
- A page that the app for iPhone, iPad and Mac shows from the web client no longer signs in with the app's own session. The app asks the instance for a stand-in (`POST api/auth/sessions/hosted`) that lasts an hour (`Auth__HostedSessionMinutes`), cannot be renewed or turned into any other credential, does not appear among signed-in devices, and stops working when the app's session is revoked or expires. The app replaces it before it ends.
- The web client can be shown inside an app that hosts it in a web view of its own: it leaves out its bars and navigation, signs in with the session the app hands it, and passes a link to a screen the app has back to the app. For such an app it can also run extensions' contributions out of sight and describe the ones built from the kit as data — and what they add to a task's menu, and an extension's whole page when that is built from the kit — so that the app can draw them itself, and it tells the app when one of them opens a dialog or says something in a snackbar. Nothing changes in a browser or in the desktop app.
- Notifications have a short title, such as "Alex assigned you a task" or "GAME-SP-12 is due in 1 week", and a line under it with more, so a phone's banner no longer cuts them off. The wording comes from a built-in set of lines, some playful, picked at random for an assignment and by the reminder itself for a deadline. The bell, browser and desktop notifications and the email digest show the same text, and notifications you already have keep theirs. The digest's plain-text version puts that line after the title, with the link on its own line.
- A project's owners can create, close, reopen and delete its boards, not only a super admin. Add board appears in the sidebar for each project you own.
- A preview server checks its own preview release for updates instead of the newest stable one, so Admin settings offers the extension versions built with that preview. Stable instances are unchanged, and `Updates__ManifestUrl` still overrides both.
- Sign-in with Microsoft Entra ID or another OIDC provider works on the web again. The content security policy only allowed Google, so the browser blocked the provider's discovery document and the sign-in ended in "Network Error"; it now allows the endpoints the instance's own provider lists in its discovery document, Entra ID's userinfo endpoint on graph.microsoft.com included, and lets silent renewal frame its authorization endpoint and come back to `/authentication/login-callback`, the one page the app may frame. Google instances keep the policy they had.
- The desktop app and the app for iPhone, iPad and Mac sign in with whatever provider the instance uses, not only Google. The app sign-in page (`/desktop-auth`) runs the authorization code flow with PKCE against a Microsoft or generic provider, with no client secret. Register `https://<instance>/desktop-auth` as a redirect URI of the provider's client: for Entra ID, under Single-page application. For those providers the page waits for you to select **Continue** before it goes to the provider, so a sign-in that some other program started cannot finish without you seeing it, and it refuses a provider whose endpoints are not https.
- The app for iPhone, iPad and Mac signs in through the system's sign-in sheet and comes back over its own address (`api/auth/desktop/start-app`), where an instance without it had the app listen on a local port for the answer, which an iPhone doesn't reliably allow while the sheet is in front.
- A theme can put a picture behind the app itself: `background` in `theme.json`, with the same fields as the sign-in background and an optional light-scheme version. The web client draws it at half strength over the page colour; a theme's stylesheet can change that with `--solo-backdrop-strength`. Ahlquist Interactive, the default theme, now has one.
- `GET api/notifications/upcoming` lists the deadline reminders a person is still to get, with the time and wording each will have, and a deadline notification carries the key of its reminder. An app can schedule those reminders on the device ahead of time, so they show while it is closed, and tell which ones it has already shown. Nothing changes in the web or desktop app.

### Desktop
- Notifications from your instance show as system notifications while the app is open.
- Every instance signs in through its sign-in page in the browser or the macOS sign-in sheet, whatever its provider, so a Microsoft or generic instance signs in as a Google one does. Settings no longer lists a per-app OIDC redirect URI: the instance's `/desktop-auth` address replaces it.
- The Mac download is the new app for the Mac, in place of the one releases stopped building: `Solo-<version>-mac.dmg`, signed and notarized, for Apple silicon and Intel on macOS 26 and later. It is a different app from the earlier one and does not update itself; a new version is a new download. A copy of the earlier app that is already installed keeps working and stays on the last version built for it; its update check still gets an answer, from that version's feed carried onto each new release.

### SDK
- Contract 1.16.0: the notification and email settings DTOs, `NotificationKind`, `DigestFrequency`, `SmtpSecurity`, `AuthConfigDto.EmailEnabled` and the core user topic `BoardHubConstants.NotificationsTopic`, used by core's own client and API.
- Contract 1.17.0: the kit, `Solo.Shared.Kit`. Ten components an extension builds a contribution from (`SoloStack`, `SoloText`, `SoloIcon`, `SoloBadge`, `SoloButton`, `SoloRow`, `SoloProgress`, `SoloToggle`, `SoloTextField`, `SoloPicker`) that say what each piece is and leave the drawing to the client; the web client draws them with the components core itself uses. A contribution built from them is one a client that is not a browser can draw too, and so is a whole page; `[WebPage]` marks a page that needs more than the kit, for such a client to show from the web client instead. `HunterAhlquist.Solo.Shared` now depends on `Microsoft.AspNetCore.Components`. Client-only: a package using the kit needs 1.17 in `soloClientVersion`.
- Contract 1.17.0 also adds `ThemeDto.Background`, the picture a theme puts behind the app.
- Contract 1.17.0 also adds `UpcomingReminderDto` and `NotificationDto.Key`, for scheduling deadline reminders on a device.

### Extensions

#### MCP Server (1.6.6)
- Rebuilt on contract 1.17.0.
- Only someone signed in to Solo can generate, rotate or revoke an MCP token. A lesser credential that Solo issues for a session, such as the short-lived one an app gives a page it shows, is refused.

#### Roadmaps (1.10.3)
- Rebuilt on contract 1.17.0.
- A project's owners can create and delete its roadmaps, not only a super admin.

#### Design Docs (1.5.6)
- Rebuilt on contract 1.17.0.
- A project's owners can create, rename, close, reopen and delete its design docs, not only a super admin.
- In the sketch editor, a two-finger tap undoes and a three-finger tap redoes. A tap has to be quick and still, so pinching and panning work as before, and the finger that lands first never leaves a stray mark.
- In a note, a task item (`- [ ] buy milk`, `- [x] done`, `1. [ ] numbered`) shows as a checkbox. Click or tap it to tick or untick it; put the caret in the box to edit it as text. Notes you can only read show the boxes but don't tick.

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
