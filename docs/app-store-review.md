# App Store review

[releases.md](releases.md) covers how a `preview-v*` tag reaches TestFlight.
This is what has to be in place before the iPad, iPhone or Mac app goes in
front of anyone outside the team: who can test it, what Apple's reviewer
needs, and the guideline questions already answered.

## Who can install a build

Preview builds upload as **internal-only** (`testFlightInternalTestingOnly` in
`src/Solo.Mobile/ios/ExportOptions-testflight.plist`; the Mac upload marks its
builds the same way). Internal builds skip App Review entirely.

- **Internal tester.** A member of the App Store Connect team. Add them under
  Users and Access with a minimal role (Customer Support or Marketing, no
  access to finances or certificates) and with app access limited to Solo
  (Preview), then add them to the internal TestFlight group. They install
  through the TestFlight app with the same Apple Account. No code change.
- **External tester.** Anyone, by email or public link, without a team seat.
  Needs the internal-only flag removed from both exports, TestFlight's Test
  Information filled in, and the first build of each version through Beta App
  Review (usually about a day). External TestFlight builds are expected to
  follow the review guidelines like a store release.

## Guideline 4.8, login services

An app that signs people in with a third-party login (Google Sign-In among
them) must also offer an equivalent login that limits data to name and email,
lets the person hide their email, and doesn't track for advertising. Sign in
with Apple is the usual answer, but not required by name.

Solo falls under the exception for "an education, enterprise, or business app
that requires the user to sign in with an existing education or enterprise
account":

- The app has no accounts of its own. It connects to an instance a team hosts.
- Nobody can sign up through it. A provider sign-in alone gets nothing; the
  instance's admin has to invite the person first (the fail-closed policy).
- The provider is the instance owner's choice (`Auth__Provider`: Google,
  Microsoft Entra ID or any OIDC issuer), not a social login offered to the
  public. Every app signs in with whatever the instance uses.

Put that argument in the review notes. If a reviewer still asks for Sign in
with Apple, it would be another instance-level provider beside the others, and
real work: Apple's OIDC needs a signed client-secret JWT and a Services ID per
instance. Build it only if review actually asks.

## A demo instance for the reviewer

The reviewer needs an instance they can sign in to. Use a separate one, never
production data.

**The instance.** A custom app on the TrueNAS (`solo-demo`), set up like
`solo-preview`: its own Postgres and data folder, an https hostname through
the proxy or the tunnel, and the server image matching the build under
review. Make sure Cloudflare shows no bot challenge to review traffic, which
comes from Apple's network: inside the sign-in sheet a challenge page reads as
a broken app.

**Sign-in.** This decides whether review passes.

- *Google with a dedicated review account* (2-step verification off, invited to
  the demo instance) is quickest. The risk is Google's "Verify it's you" check
  on a new device and location, which can ask for a phone the reviewer doesn't
  have, and an app the reviewer can't sign in to is a guideline 2.1 rejection.
- *An OIDC provider you control* (Keycloak or Authentik on the TrueNAS,
  `Auth__Provider=generic`) with a plain username and password has no device
  challenges. This is the recommended route. It is also the end-to-end test
  of the generic sign-in path that hasn't had one: register
  `https://<demo>/authentication/login-callback` and `https://<demo>/desktop-auth`
  as redirect URIs.

Either way, the reviewer is an owner of a demo project, not a super admin.

**Content.** After the setup wizard, one demo project with a board or two of
realistic tasks, a design doc with a sketch, and a roadmap, so the native
features have something to act on. Snapshot the dataset before review and
roll back afterwards.

**Pointing the app at it.** Set the repository variable
`SOLO_IOS_INSTANCE_URL` to the demo address for the submission build, so the
reviewer lands on its sign-in page rather than on "Add your first instance".
Put the address in the review notes too, in case they reinstall.

## Review notes

- The demo address, the reviewer's username and password, and the steps: open
  the app, tap sign in, enter the credentials.
- The 4.8 sentence: Solo is a self-hosted business app; the app has no accounts
  of its own; people sign in with the identity provider their team's instance
  uses, and only after the instance's admin has invited them.
- What to try: open a design doc's sketch on an iPad and double-tap the Apple
  Pencil to swap to the eraser and back.
