# Solo Mobile

The iPad and iPhone app, built with [Capacitor](https://capacitorjs.com/docs) 8.
It opens a Solo instance in its web view and adds what a web page can't get:
the Apple Pencil double-tap, Google sign-in, and a list of instances to switch
between.

The app runs the client the instance serves rather than a bundled copy,
because the client and its extensions use same-origin relative URLs
everywhere: REST, SignalR, images and extension assets. It loads it through a
local origin, as the desktop app does (see below), and points Capacitor's
`server.url` at that. Capacitor's docs call `server.url` "not intended for use
in production" ([config reference](https://capacitorjs.com/docs/config)), so
treat it as the part of this setup most likely to change.

## The local origin

The page lives on `http://localhost:<port>`, not on the instance's address.
`App/App/LocalProxy.swift` is a loopback server that forwards every path, the
client's own files included, to the instance, like the desktop app's
`main/server.js`:

- **Why:** a WKWebView only gets service workers on an App-Bound Domain, and
  the client's offline mode (a vault kept on this device) needs them. An app
  lists its App-Bound Domains in Info.plist (`WKAppBoundDomains`, at most ten)
  when it's built, and every instance is somewhere else, so the one entry is
  `localhost`. `limitsNavigationsToAppBoundDomains` is on, which WebKit
  requires for an app-bound web view to keep its message handlers and user
  scripts; links to anywhere else open in Safari as before. `http://localhost`
  is also a secure context, so a plain-HTTP instance on the LAN gets
  `crypto.randomUUID()` and the rest.
- **One port per instance**, from 43321 up (`InstancePorts`, saved in user
  defaults), so each instance keeps its own origin, and with it its storage
  and session, across launches. The range stays clear of the desktop app's
  (41321 up): an iOS simulator, or the app running on a Mac, shares the Mac's
  loopback. A port is never handed to a second instance,
  which would open signed in as the first, with its offline edits. When
  another app holds an instance's port, it moves to a fallback port of its own
  (signed in separately), and stays there until that port is held in turn:
  each move leaves the old origin's session and unsynced edits behind until
  the instance opens there again, so it doesn't move back just because the
  main port came free. `scripts/check-instance-ports.sh` checks these rules (CI
  runs it).
- **Loopback only.** The listener binds to 127.0.0.1 and refuses any other
  peer, and a request whose `Host` isn't `localhost:<port>` or
  `127.0.0.1:<port>` gets a 403, the desktop app's DNS-rebinding guard.
- **HTTP** goes upstream through URLSession and streams back chunked, with
  `Origin`, `Referer` and redirects rewritten between the two origins.
  **WebSockets** (SignalR's hub) are a raw TCP or TLS tunnel once the upgrade's
  head is rewritten.
- **When the instance can't be reached**, a page load gets a small "Can't
  reach the instance" page (Try again, Instances) and a fetch gets a JSON 502
  the client already handles. Once a vault is kept offline, the client's
  service worker answers page loads before they reach the proxy.
- **Network changes:** a change of network path (airplane mode, Wi-Fi coming
  back) drops the upstream connections and restarts the listener if it died,
  so nothing waits on a connection that went away with the old network.
- **Background and lock:** iOS defuncts a suspended app's listener without
  telling it. The `NWListener` still reports `.ready` and simply stops
  accepting, which is how locking the device used to leave the page unable to
  reconnect until the app was relaunched. So the proxy closes the listener when
  the app goes to the background and opens a new one on the same port when it
  comes back (TN2277's advice). The client keeps retrying its live connection
  every five seconds after SignalR's own reconnect gives up, so it recovers
  however long that took.

## Instances

The app keeps its own list of instances, like the desktop app. "Instances" in
the profile menu, or "Use another instance" on the sign-in page, posts the
fixed message `request_instance-settings`, and a native sheet
(`App/App/Instances.swift`) lists them. Adding one checks
`api/config/auth` first. Opening one rebuilds the view controller on it,
because Capacitor reads the address once, at load
(`SoloViewController.instanceDescriptor()`), which also starts the instance's
proxy. Each instance's session stays in the web view's storage for its local
origin, so switching back finds it signed in.

`SOLO_INSTANCE_URL` at build time is optional. When set, that instance joins
the list the first time a build carries it, and opens. Without it, and with
nothing saved yet, the app opens on the list as its setup page: "Add your first
instance", with a link to the install guide, and it can't be dismissed until an
instance is open. The desktop app's Settings window does the same on first run.

## Apple Pencil double-tap

Safari has no event for the double-tap; only UIKit's `UIPencilInteraction`
reports it. `App/App/SoloViewController.swift` attaches one to the web view
and, when the person's Pencil setting in iPadOS is to switch to the eraser or
to the previous tool, dispatches this on the page:

```js
window.dispatchEvent(new CustomEvent('solo:device-action', { detail: { id: 'action_stylus-tool-swap' } }))
```

The client's `solo.deviceActions` hands it to `ISoloDeviceActions`, where a
page or an extension has registered for `DeviceActions.StylusToolSwap`
(contract 1.17). Design Docs' sketch uses it to swap to the eraser and back.

The direction is deliberate. Native code only pushes a generic action name into
the page; nothing in the page, and nothing in an extension, can call native
code through it. That keeps extensions, which load at runtime, away from App
Review guideline 4.7.2 ("may not extend or expose native platform APIs or
technologies to the software"), and the same name can come from another
platform's control later.

## Notifications

The app shows no notifications. A WKWebView has no Notification API, so the
client's OS notifications (`wwwroot/js/notifications.js`) report "unsupported"
here, and new entries show on the bell and the in-page bar while the app is
open. Away from the app, the email digest is the only notification.

## Widgets

`ios/App/Widgets` is a WidgetKit extension (iOS 17 or later; the app itself
still runs on 15) with three Home Screen widgets, drawn in the Ahlquist
Interactive theme whatever theme the instance uses:

- **Assigned** (small): how many of your tasks are new or in progress, on a
  two-digit seven-segment display. Opens Your Tasks.
- **Assigned Work** (medium): those tasks as coloured squares under New,
  In progress and Done, filled when due within two days or overdue, soonest
  deadline first. Done counts what the Your Tasks board shows. Opens Your Tasks.
- **Upcoming Due** (large): up to five tasks due within 7 days, overdue first,
  each with a ring of the days left. A row opens its task; past five, the last
  row is "+N more", which opens Your Tasks.

Each widget shows one instance, picked in its settings and the current one by
default. All three read `GET api/users/me/work-summary`. The bottom-right
smile is a refresh button (an App Intent); otherwise each widget refreshes
about every 30 minutes, at midnight, and whenever the app changes its
instances or goes to the background.

The app and the extension share an App Group, `group.<bundle id>`
(`App/App.entitlements`, `Widgets/Widgets.entitlements`; `SoloAppGroup` in
each Info.plist names it at runtime), through `ios/App/Shared`, which both
targets compile:

- **The instance list** is copied into the group's defaults whenever the app
  changes it (`SavedInstances.publish()`).
- **Each instance's session** is copied into a Keychain item in the group's
  access group, readable after first unlock and on this device only. The page
  hands it over as it stores or clears it (`solo-interop.js` →
  `soloApp.keepSession`). Signing out, a 401 from the summary and removing the
  instance delete it, and the widget then asks you to sign in. Every change
  to a session, and a widget's use of what it fetched with one, happens under
  one lock (a `flock` in the group container), so an answer that arrives after
  the session changed is dropped rather than written over the new one.
- **The last summary** is cached in the group container, so a widget that can't
  reach its instance keeps showing what it last saw. It goes whenever the
  session changes.

Taps open the app with `solo-widget://open?instance=…&path=…`, built and
checked in `Shared/WidgetLink.swift`. The scheme isn't registered, since
WidgetKit hands a widget's links to its own app directly. The app ignores an
instance it doesn't have and any path that isn't relative to the instance,
then opens the path in place, or switches instances first.

The extension's bundle id is the app's plus `.widgets`. Signing it needs both
App IDs and the App Group registered under the team, for the preview
(`com.hunterahlquist.solo.preview`) and the store bundle id alike. With
`-allowProvisioningUpdates`, automatic signing registers what's missing where
it can.

## Sign-in

Whatever the instance's provider, the app signs in the way the desktop app
does: Google won't run its sign-in inside an app's web view, and the web view
opens any other provider's host in Safari. The page defines nothing native but one fixed
message: the sign-in button posts `request_sign-in` to `window.soloApp`, and
`App/App/AppSignIn.swift` takes it from there:

1. `POST api/auth/desktop/start-app` with a PKCE challenge opens a flow.
2. The instance's `/desktop-auth` page runs the provider's sign-in (Google's,
   or the code flow against a Microsoft or generic OIDC provider) in the system
   sign-in sheet (`ASWebAuthenticationSession`) and returns to
   `com.hunterahlquist.solo:/auth/callback` with a one-time code.
3. `POST api/auth/desktop/redeem` trades the code and the verifier for a
   `solo_` session, which the page stores like the desktop app's.

Providers only return to addresses registered for the instance's client id
(for Microsoft and generic providers, `https://<instance>/desktop-auth`), so
this needs the instance's real HTTPS address. A plain-HTTP address on the local
network can't sign in this way; see [Testing against a local
instance](#testing-against-a-local-instance).

## Building on macOS

You need Node.js 22 or later and Xcode 26 or later, with its command line
tools. The project uses Swift Package Manager, so CocoaPods isn't needed.

```sh
cd src/Solo.Mobile
npm install
export SOLO_INSTANCE_URL=https://solo.example.com   # optional
npm run sync    # writes the instance into the Xcode project
npm run open    # Xcode: pick your team under Signing & Capabilities, then Run
```

Run `npm run sync` again whenever `SOLO_INSTANCE_URL`, `capacitor.config.js`
or `www/` changes. The generated `capacitor.config.json` holds the instance
address and is ignored by git.

To build and install on a connected iPad without opening Xcode:

```sh
cd ios/App
xcodebuild -project App.xcodeproj -scheme App -configuration Debug \
  -destination 'generic/platform=iOS' -derivedDataPath build \
  -allowProvisioningUpdates DEVELOPMENT_TEAM=<your team id> build
xcrun devicectl device install app --device <device id> \
  build/Build/Products/Debug-iphoneos/App.app
```

`xcrun devicectl list devices` shows the device id.

## Status bar and safe areas

The web view reaches every edge of the screen (`contentInset: 'never'` in
`capacitor.config.js`), and the page keeps its own content clear of the status
bar, the Dynamic Island and the home indicator, with `viewport-fit=cover` and
`env(safe-area-inset-*)` in the client's `app.css`. That is how it already
behaves in Safari, and it means the app bar's own colour sits behind the status
bar and a drawer's scrim covers it too, with nothing native painted there to
fall out of step. The one thing the app still does is pick light or dark status
bar text against the app bar, whose colour the client publishes as the page's
`theme-color` (`WKWebView.themeColor`).

## App icon

The app uses the same Icon Composer files as the desktop app,
`Icons/Solo.icon` and `Icons/Solo Preview.icon` at the repository root. They
carry the light, dark and tinted appearances, and iPadOS picks one from the
person's Home Screen icon setting. Debug builds use the Preview icon and the
name "Solo (Preview)", as the desktop app's preview does, and Release builds
the production ones (`ASSETCATALOG_COMPILER_APPICON_NAME` and
`SOLO_DISPLAY_NAME` in each configuration), so a development build is easy to
tell apart on the Home Screen. Debug builds also take the preview's bundle id,
`com.hunterahlquist.solo.preview`, so a development build and a TestFlight
preview are the same app, apart from a store install.

`npm run sync` stages them into `ios/App/Icons` (git-ignored), which is what
the Xcode project compiles. Staging drops any icon feature the installed Xcode
can't compile, because an Xcode that predates one (Xcode 26 and
`refractivity`) crashes instead of drawing the icon without it. The icon then
builds without that effect, and a newer Xcode brings it back.
`scripts/stage-icons.mjs` reuses the desktop app's `stage-icon.mjs` for this.

## TestFlight previews

A `preview-v*` tag runs `.github/workflows/release-ios.yml`, which archives a
Release build under the preview name, icon and bundle id
(`com.hunterahlquist.solo.preview`, its own App Store Connect app) and
uploads it for internal testers only (`ios/ExportOptions-testflight.plist`). It
can also be run by hand on a branch. Signing is Xcode's automatic signing with
an App Store Connect API key, so there's no certificate to export.

One-time setup:

1. In App Store Connect, create the app for
   `com.hunterahlquist.solo.preview`, named "Solo (Preview)", and accept
   any pending agreements.
2. Create a team API key (Users and Access → Integrations → App Store Connect
   API) with the Admin role, which cloud-managed signing needs. The `.p8` can
   only be downloaded once.
3. Create a GitHub environment named `testflight` with a deployment tag rule
   for `preview-v*`, holding the secrets `APP_STORE_CONNECT_KEY` (the `.p8`
   file's contents), `APP_STORE_CONNECT_KEY_ID` and
   `APP_STORE_CONNECT_ISSUER_ID`. `APPLE_TEAM_ID` is the repository secret the
   desktop release already uses.
4. In TestFlight, add an internal group with automatic distribution, so each
   processed build reaches its testers without a manual step.

Optionally, the repository variable `SOLO_IOS_INSTANCE_URL` gives the build an
instance to open first. Without one it opens on the instance list. Either way
the instance has to run a server with the app's sign-in (`start-app`).

Without the secrets, the run stops at a notice. A Design Docs package preview
tag (`…-design-docs-<label>.N`) doesn't upload the app.

Nor does a tag that leaves the app as testers already have it, which is most
preview tags: the app loads its instance, so a server or client change doesn't
reach it. `build/testflight-gate.sh` diffs the app's inputs against the commit
of the last run whose Upload step succeeded and, when nothing changed, ends the
run with a notice in its summary. The Mac TestFlight upload uses the same gate,
and a client change skips it too, since the desktop app proxies the client its
instance serves. A manual run always uploads, and so does any run where the
gate can't tell.

## Testing against a local instance

Google won't sign in on a plain-HTTP address. Debug builds can start signed
in instead, with a `solo_` session minted on the instance, the same recipe as
the `verify` skill:

1. Insert an `AuthSessions` row for your user with the SHA-256 of a new
   `solo_…` token, as the `verify` skill describes.
2. Write `ios/dev.xcconfig`, which git ignores:

   ```
   SOLO_DEV_SESSION_TOKEN = solo_…
   SOLO_DEV_SESSION_USER = <base64url of GET api/users/me with that token>
   ```

   The user is base64url, not JSON, because an xcconfig value ends at `//`.
3. Build the Debug configuration. `App/App/DevShim.swift` writes the session
   into local storage before the page loads.

A plain `http://` address on the local network works for this: the page is on
the local origin either way, which is a secure context. The session goes only
to the built-in instance's local origin, and the script doesn't exist in a
Release build.

Delete the `AuthSessions` row when you're done testing.
