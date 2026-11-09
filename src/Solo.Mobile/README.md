# Solo Mobile

The iPad and iPhone app, built with [Capacitor](https://capacitorjs.com/docs) 8.
It opens one Solo instance in its web view and adds what a web page can't get:
the Apple Pencil double-tap.

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

## Testing against a local instance

Google sign-in doesn't run inside an app's web view, and in-app sign-in isn't
built yet. Debug builds can start signed in instead, with the client's login
shim (`js/dev-desktop.js`) and a `solo_` session minted on the instance, the
same recipe as the `verify` skill:

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
