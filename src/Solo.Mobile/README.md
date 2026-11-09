# Solo Mobile

The iPad and iPhone app, built with [Capacitor](https://capacitorjs.com/docs) 8.
It opens one Solo instance in its web view and adds what a web page can't get:
the Apple Pencil double-tap, and Google sign-in.

The app loads the instance itself (`server.url` in `capacitor.config.js`)
rather than a bundled copy of the client, because the client and its
extensions use same-origin relative URLs everywhere: REST, SignalR, images and
extension assets. The instance is chosen at build time with `SOLO_INSTANCE_URL`.
Capacitor's docs call `server.url` "not intended for use in production"
([config reference](https://capacitorjs.com/docs/config)), so treat it as the
part of this setup most likely to change.

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
(contract 1.16). Design Docs' sketch uses it to swap to the eraser and back.

The direction is deliberate. Native code only pushes a generic action name into
the page; nothing in the page, and nothing in an extension, can call native
code through it. That keeps extensions, which load at runtime, away from App
Review guideline 4.7.2 ("may not extend or expose native platform APIs or
technologies to the software"), and the same name can come from another
platform's control later.

## Sign-in

Google won't run its sign-in inside an app's web view, so the app signs in the
way the desktop app does. The page defines nothing native but one fixed
message: the sign-in button posts `request_sign-in` to `window.soloApp`, and
`App/App/AppSignIn.swift` takes it from there:

1. `POST api/auth/desktop/start-app` with a PKCE challenge opens a flow.
2. The instance's `/desktop-auth` page runs Google's sign-in in the system
   sign-in sheet (`ASWebAuthenticationSession`) and returns to
   `io.github.hunterahlquist.solo:/auth/callback` with a one-time code.
3. `POST api/auth/desktop/redeem` trades the code and the verifier for a
   `solo_` session, which the page stores like the desktop app's.

Google only runs on origins registered for the instance's client id, so this
needs the instance's real HTTPS address. A plain-HTTP address on the local
network can't sign in this way; see [Testing against a local
instance](#testing-against-a-local-instance).

## Building on macOS

You need Node.js 22 or later and Xcode 26 or later, with its command line
tools. The project uses Swift Package Manager, so CocoaPods isn't needed.

```sh
cd src/Solo.Mobile
npm install
export SOLO_INSTANCE_URL=https://solo.example.com
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

## App icon

The app uses the same Icon Composer files as the desktop app,
`Icons/Solo.icon` and `Icons/Solo Preview.icon` at the repository root. They
carry the light, dark and tinted appearances, and iPadOS picks one from the
person's Home Screen icon setting. Debug builds use the Preview icon and
Release builds the production one (`ASSETCATALOG_COMPILER_APPICON_NAME` in each
configuration), so a development build is easy to tell apart on the Home
Screen.

`npm run sync` stages them into `ios/App/Icons` (git-ignored), which is what
the Xcode project compiles. Staging drops any icon feature the installed Xcode
can't compile, because an Xcode that predates one (Xcode 26 and
`refractivity`) crashes instead of drawing the icon without it. The icon then
builds without that effect, and a newer Xcode brings it back.
`scripts/stage-icons.mjs` reuses the desktop app's `stage-icon.mjs` for this.

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

A plain `http://` address on the local network works for this. iPadOS only
gives `crypto.randomUUID()` to secure pages, and Design Docs needs it, so the
same debug-only script fills it in. Neither part exists in a Release build.

Delete the `AuthSessions` row when you're done testing.
