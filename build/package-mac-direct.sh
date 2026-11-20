#!/usr/bin/env bash
# Signs an archived Mac app for download outside the App Store, has Apple
# notarize it, and wraps it in the disk image a release offers.
#
#   package-mac-direct.sh <Solo.xcarchive> <out.dmg> [<name>]
#
# <name> is what the app and the disk image's volume are called, without
# `.app`; left out, it keeps the archive's. $APP_IDENTITY (Developer ID
# Application) signs both, looked up by name in the keychain search list.
#
# Notarizing needs Apple's say-so, asked with one of:
#   $NOTARY_PROFILE                      a notarytool keychain profile, on a Mac
#                                        that has one stored
#   $APPLE_ID, $APPLE_APP_SPECIFIC_PASSWORD and $APPLE_TEAM_ID
#                                        the same three as the release workflow
# With neither it stops, unless $ALLOW_UNNOTARIZED is 1: then the image is made
# signed and not notarized, for looking at on the Mac that built it. Gatekeeper
# turns that one away anywhere else, so it is never what a release should hold.
#
# The archive comes in signed for nobody (release-desktop.yml), carrying only
# its sandbox entitlements, as the Mac App Store build's does
# (package-mac-store.sh). That one is signed for the store and can only be
# opened through it; this is the same app for anyone who downloads it:
#   - the hardened runtime, which notarization requires;
#   - no provisioning profile and no application identifier, which a Developer
#     ID app has neither of. The app asks for nothing that needs them: its
#     session is kept in the login keychain, not the data-protection one;
#   - a ticket stapled to the app itself and then to the image, so each opens
#     on a Mac that can't reach Apple — the app after it has been dragged out
#     of the image, which is where a ticket on the image alone doesn't follow.
set -euo pipefail

archive=${1:?Usage: package-mac-direct.sh <archive> <out.dmg> [<name>]}
out=${2:?Usage: package-mac-direct.sh <archive> <out.dmg> [<name>]}
name=${3:-}
identity=${APP_IDENTITY:-Developer ID Application}
# How long to wait on the notary service for each of the two submissions.
# Apple carries on processing after this; it only stops a runner watching.
wait_for=${NOTARY_TIMEOUT:-30m}

die() { echo "::error::$1" >&2; exit 1; }
value() { /usr/libexec/PlistBuddy -c "Print :$2" "$1" 2>/dev/null; }

shopt -s nullglob
apps=("$archive"/Products/Applications/*.app)
[ ${#apps[@]} -eq 1 ] || die "Expected one app in $archive, found ${#apps[@]}."

auth=()
if [ -n "${NOTARY_PROFILE:-}" ]; then
    auth=(--keychain-profile "$NOTARY_PROFILE")
elif [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_APP_SPECIFIC_PASSWORD:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ]; then
    auth=(--apple-id "$APPLE_ID" --password "$APPLE_APP_SPECIFIC_PASSWORD" --team-id "$APPLE_TEAM_ID")
elif [ "${ALLOW_UNNOTARIZED:-}" != 1 ]; then
    die "Nothing to notarize with: set NOTARY_PROFILE, or APPLE_ID, APPLE_APP_SPECIFIC_PASSWORD and APPLE_TEAM_ID."
fi

# A copy, so the archive stays as Xcode made it and a second run starts clean.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
name=${name:-$(basename "${apps[0]}" .app)}
mkdir "$work/image"
app="$work/image/$name.app"
ditto "${apps[0]}" "$app"

# One signature covers the app as it is built today: a single binary, its
# packages linked in. Anything that is code of its own needs signing first,
# inside out, so stop instead of shipping it with the signature it came with.
for nested in Frameworks PlugIns XPCServices Helpers Library; do
    [ -z "$(ls -A "$app/Contents/$nested" 2>/dev/null)" ] ||
        die "The app has Contents/$nested, which this script doesn't sign. Teach it to before releasing."
done

# What the app was built to ask for, read back from its signature rather than
# from the project so it is what Xcode resolved. The identifiers the store's
# build is given are taken out if they are there: without a profile to allow
# them, macOS won't launch an app that claims them.
codesign -d --entitlements - --xml "$app" > "$work/entitlements.plist" 2>/dev/null || true
[ "$(value "$work/entitlements.plist" com.apple.security.app-sandbox)" = true ] ||
    die "The archived app isn't sandboxed. It is built to be; this isn't the archive the workflow makes."
edit() { /usr/libexec/PlistBuddy -c "$1" "$work/entitlements.plist" > /dev/null 2>&1; }
edit "Delete :com.apple.security.get-task-allow" || true
edit "Delete :com.apple.application-identifier" || true
edit "Delete :com.apple.developer.team-identifier" || true

codesign --force --options runtime --timestamp --sign "$identity" \
    --entitlements "$work/entitlements.plist" "$app"
codesign --verify --strict --deep "$app"
bundle_id=$(value "$app/Contents/Info.plist" CFBundleIdentifier)

# Sends a file to the notary service and waits for its verdict. The verdict is
# the last `status:` line of submit's own output, not its exit status, which a
# timeout also produces. It isn't asked for again with `notarytool info`: that
# is one more authenticated call, able to fail a build Apple already accepted.
notarize() {
    local file=$1 log="$work/notary.log" id status
    echo "Submitting $(basename "$file") ($(du -h "$file" | cut -f1 | tr -d ' ')) to the notary service." >&2
    xcrun notarytool submit "$file" "${auth[@]}" --wait --timeout "$wait_for" 2>&1 | tee "$log" >&2 || true
    id=$(awk '$1 == "id:" { print $2; exit }' "$log")
    [ -n "$id" ] || die "The notary service returned no submission id; notarytool's output is above."
    status=$(awk '/^ *status: / { sub(/^ *status: /, ""); s = $0 } END { print s }' "$log")
    case "$status" in
        Accepted) ;;
        "" | "In Progress")
            die "Apple was still processing submission $id after $wait_for. It wasn't abandoned, but this build can't wait for it; run it again to submit again." ;;
        *)
            xcrun notarytool log "$id" "${auth[@]}" >&2 || true
            die "Notarization of submission $id ended as '$status'; Apple's log is above." ;;
    esac
}

if [ ${#auth[@]} -gt 0 ]; then
    # The service takes an archive, not a folder. The ticket it issues is for
    # the app, which is what it is stapled to.
    ditto -c -k --sequesterRsrc --keepParent "$app" "$work/app.zip"
    notarize "$work/app.zip"
    xcrun stapler staple "$app" >&2
    # Gatekeeper's own verdict, from the stapled ticket.
    spctl -a -t exec -vv "$app" 2>&1 | tee "$work/spctl.log" >&2
    grep -q "source=Notarized Developer ID" "$work/spctl.log" ||
        die "Gatekeeper doesn't take the app as notarized; its answer is above."
fi

# The image: the app, and the folder to drag it to. Made after the ticket is
# stapled, so the app inside it carries one. ULMO is the smallest of the
# formats, and every macOS the app runs on reads it.
ln -s /Applications "$work/image/Applications"
mkdir -p "$(dirname "$out")"
rm -f "$out"
hdiutil create -quiet -volname "$name" -srcfolder "$work/image" -fs HFS+ -format ULMO "$out"
codesign --force --timestamp --sign "$identity" "$out"

if [ ${#auth[@]} -gt 0 ]; then
    notarize "$out"
    xcrun stapler staple "$out" >&2
    spctl -a -t open --context context:primary-signature -vv "$out" 2>&1 | tee "$work/spctl.log" >&2
    grep -q "source=Notarized Developer ID" "$work/spctl.log" ||
        die "Gatekeeper doesn't take the disk image as notarized; its answer is above."
    echo "Packaged $name.app ($bundle_id), signed and notarized, as $out." >&2
else
    echo "::warning::Packaged $name.app ($bundle_id) as $out, signed but NOT notarized: Gatekeeper turns it away on any other Mac." >&2
fi
