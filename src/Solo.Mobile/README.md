# Solo Mobile

iOS and Android apps for one Solo instance, built with
[Capacitor](https://capacitorjs.com/docs) 8.

The app opens the instance itself in its web view (`server.url` in
`capacitor.config.js`), so the client runs exactly as it does in a mobile
browser: same origin, same REST and SignalR, same extension pages. The
alternative, bundling the published client the way the desktop app does, would
need the API to accept cross-origin calls and every relative URL in the client
and its extensions (images, avatars, extension assets) to learn about a second
origin. The instance is chosen at build time, from `SOLO_INSTANCE_URL`.

Capacitor's docs describe `server.url` as meant for live reload and "not
intended for use in production"
([config reference](https://capacitorjs.com/docs/config)). It is what makes
Capacitor inject its bridge into the instance's pages, which the sign-in below
needs, and plenty of shipped apps rely on it, but it is the part of this setup
most likely to need revisiting.

## Building on macOS

You need:

- Node.js 22 or later.
- Xcode 26 or later, with its command line tools (`xcode-select --install`).
  iOS uses Swift Package Manager, so CocoaPods isn't needed.
- Android Studio 2025.2.1 or later, with an SDK platform at API 24 or above. It
  brings its own JDK.

Those are Capacitor 8's minimums
([environment setup](https://capacitorjs.com/docs/getting-started/environment-setup)).

```sh
cd src/Solo.Mobile
npm install
export SOLO_INSTANCE_URL=https://solo.example.com
npm run sync            # writes the instance into both native projects
npm run open:ios        # Xcode: pick a team under Signing & Capabilities, then Run
npm run open:android    # Android Studio: Run, or Build > Generate Signed App Bundle
```

Run `npm run sync` again whenever `SOLO_INSTANCE_URL`, `capacitor.config.js`,
`www/` or a Capacitor plugin changes. The generated `capacitor.config.json`
files hold the instance address and are ignored by git, so a checkout never
carries anyone's instance.

`npm run run:ios` and `npm run run:android` build and launch on a simulator,
emulator or connected device without opening the IDE.

An `http://` address works for testing against a local server. It turns on
cleartext traffic on Android, so use `https://` for anything you hand out.

### Icons and splash screens

The sources are in `assets/`, made from the desktop icon. To regenerate the
native images after changing them:

```sh
npx @capacitor/assets generate --ios --android
```

## Sign-in

Google blocks its sign-in pages in embedded web views
([Google Developers Blog](https://developers.googleblog.com/en/upcoming-security-changes-to-googles-oauth-20-authorization-endpoint-in-embedded-webviews/)),
the same rule that keeps it out of Electron. So the app runs the desktop app's
browser sign-in, with one change: the browser comes back to the app through its
URL scheme, `io.github.hunterahlquist.solo:/auth/callback`, rather than to a
loopback port, which an app can't keep listening on while the browser is in
front.

1. The client (`solo.mobile` in `solo-interop.js`) makes a PKCE verifier and
   opens a flow with `POST api/auth/desktop/start-app`.
2. It opens `/desktop-auth?state=…` in the system browser (Safari View
   Controller on iOS, Custom Tabs on Android), where Google sign-in is allowed.
3. The instance sends a one-time code to the app's URL scheme. The client
   redeems it with the verifier (`POST api/auth/desktop/redeem`) for a Solo
   session.

Any app can register the same scheme, which is why the code alone is worthless:
redeeming it takes the verifier, and that never leaves the app's web view
([RFC 8252 §7.1 and §8.1](https://www.rfc-editor.org/rfc/rfc8252)).

Instances that sign in through another OIDC provider run its redirect inside
the web view. Whether that works depends on the provider's web view policy.

## App Store review

Apple's guideline 4.2 rejects apps that are mostly a repackaged website
([App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/#minimum-functionality)).
This app is that today. Native features such as push notifications and haptics
are the usual way past it, and they fit this setup: each is a Capacitor plugin
(`@capacitor/push-notifications`, `@capacitor/haptics`) installed here and
called from the client through `window.Capacitor.Plugins`, the way sign-in
already calls `Browser` and `App`. Builds for internal TestFlight testers, and
Android builds you install yourself, don't go through that review.
