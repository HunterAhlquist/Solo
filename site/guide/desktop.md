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

## Connect to an instance

The first time the app starts, it shows the **Server Settings** window.

1. Enter the address of the instance, for example `https://solo.example.com`.
2. Select **Test**. The app checks that the address is a Solo instance.
3. Save the settings.

To change the instance later, select **Solo**, then **Server Settings…** in the menu. The shortcut is <kbd>Cmd</kbd>+<kbd>,</kbd> on a Mac and <kbd>Ctrl</kbd>+<kbd>,</kbd> on Windows and Linux.

## Sign in

Sign-in for the desktop app uses your default browser.

1. Select **Authorize in browser** in the app.
2. Complete the sign-in in the browser page that opens.
3. Go back to the app. It shows your projects.

The app stays signed in. The session ends after 30 days with no use, unless the administrator set a different period.

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

The app runs the client from a local address, `http://localhost:41321`, and sends API requests to your instance.

- **Google.** No change is necessary. Sign-in runs on the address of your instance, which the identity provider knows.
- **Other identity providers.** Add `http://localhost:41321/authentication/login-callback` as a redirect address for the OIDC client.

The session period is the `Auth__SessionDays` setting. See [Configuration](../admin/configuration.md).
