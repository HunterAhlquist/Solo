---
title: Desktop app
lede: The desktop app shows the same Solo in its own window on macOS, Windows and Linux.
---

## What the desktop app is

The desktop app is a window around the Solo web client. It connects to a Solo instance that you select. It has the same pages and the same behaviour as Solo in a browser.

## Install

1. Open the [releases page](https://github.com/HunterAhlquist/Solo/releases) of the repository.
2. Find the newest release with a name that starts with `desktop-v`.
3. Download the installer for your system:

| System | File |
|---|---|
| macOS (Apple silicon) | `.dmg` |
| Windows | Installer `.exe` |
| Linux | `.AppImage` |

## Add an instance

The first time the app starts, it shows the **Settings** window.

1. Enter the address of the instance, for example `https://solo.example.com`.
2. Select **Add**. The app checks that the address is a Solo instance. If the check fails, the app does not add the instance.
3. Complete the sign-in in the browser page that opens.
4. Go back to the app. The instance opens in a new window.

To add a different instance later, select **Solo**, then **Settings…** in the menu. On Windows and Linux, the entry is in the **File** menu. The shortcut is <kbd>Cmd</kbd>+<kbd>,</kbd> on a Mac and <kbd>Ctrl</kbd>+<kbd>,</kbd> on Windows and Linux.

The app shows each instance by its display name. An administrator sets the name in the [Admin settings](../admin/admin-settings.md#branding). An instance with no display name shows by its address.

## Open an instance

Each instance has one window at most.

1. Select **Solo**, then **Open Instance** in the menu.
2. Select an instance. An instance that is open is not available in the list. Use the **Window** menu to go to its window.
3. Select **New Window** or **Current Window**. **Current Window** replaces the instance in the window that you used last.

When the app starts, it opens the instance that you used last. The app closes when you close its last window.

## Remove an instance

1. Select **Solo**, then **Settings…** in the menu.
2. Select **Remove** next to the instance, and confirm.

The app closes the window of the instance and deletes its sign-in from this computer. The session stays on the instance. To end the session, sign out in the app before you remove the instance, or use [Signed-in devices](#signed-in-devices).

## Sign in

Sign-in for the desktop app uses your default browser. The app starts the sign-in when you add an instance.

To sign in again later:

1. Select **Authorize in browser** in the window of the instance.
2. Complete the sign-in in the browser page that opens.
3. Go back to the app. It shows your projects.

The app stays signed in to each instance. The session ends after 30 days with no use, unless the administrator set a different period.

## Signed-in devices

You can see each desktop app that has a session for your account, and sign one out.

1. Select your account picture in the app bar.
2. Select **Signed-in devices**.
3. Select **Sign out** next to a device. Or select **Sign out all other devices**.

A device that you sign out must sign in again. The list shows desktop apps only. A browser signs in through the identity provider each time and is not in the list.

## Keep the app current

The desktop app contains its own copy of the Solo client. That copy can be older than the instance.

- An extension page can be missing in the desktop app while it shows in a browser. The extension needs a newer client. Install the newest desktop release.
- If sign-in fails after an update of the instance, install the newest desktop release.

## For administrators

The app runs the client from a local address and sends API requests to your instance. Each instance in the app has its own local address. The first is `http://localhost:41321`, and the port number increases by one for each instance that a person adds. The **Settings** window shows the local address below each instance.

- **Google.** No change is necessary. Sign-in runs on the address of your instance, which the identity provider knows.
- **Other identity providers.** Add the local address with `/authentication/login-callback` as a redirect address for the OIDC client, for example `http://localhost:41321/authentication/login-callback`. The local address can be different on each computer, because it depends on the sequence in which the person added their instances.

The session period is the `Auth__SessionDays` setting. See [Configuration](../admin/configuration.md).
