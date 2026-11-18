#!/usr/bin/env bash
# Signs an archived Mac app for the Mac App Store and wraps it in the installer
# package App Store Connect takes.
#
#   package-mac-store.sh <Solo.xcarchive> <profile.provisionprofile> <out.pkg> [<name>]
#
# <name> is what the app is called in /Applications, without `.app`; left out,
# it keeps the archive's. The two identities are looked up by name in the
# keychain search list: $APP_IDENTITY (Apple Distribution) signs the app and
# $INSTALLER_IDENTITY (Mac Installer Distribution, which the keychain lists as
# "3rd Party Mac Developer Installer") signs the package.
#
# The archive comes in signed for nobody (release-mac-store.yml), carrying only
# its sandbox entitlements. Everything that makes it the store's happens here,
# in steps that can each be run and looked at: `xcodebuild -exportArchive` does
# the same from an export plist, but only says what it made of the certificates
# and profile once Apple has the build.
set -euo pipefail

archive=${1:?Usage: package-mac-store.sh <archive> <profile> <out.pkg> [<name>]}
profile=${2:?Usage: package-mac-store.sh <archive> <profile> <out.pkg> [<name>]}
out=${3:?Usage: package-mac-store.sh <archive> <profile> <out.pkg> [<name>]}
name=${4:-}
app_identity=${APP_IDENTITY:-Apple Distribution}
installer_identity=${INSTALLER_IDENTITY:-3rd Party Mac Developer Installer}

die() { echo "::error::$1" >&2; exit 1; }
value() { /usr/libexec/PlistBuddy -c "Print :$2" "$1" 2>/dev/null; }

shopt -s nullglob
apps=("$archive"/Products/Applications/*.app)
[ ${#apps[@]} -eq 1 ] || die "Expected one app in $archive, found ${#apps[@]}."
[ -f "$profile" ] || die "No provisioning profile at $profile."

# A copy, so the archive stays as Xcode made it and a second run starts clean.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
app="$work/${name:-$(basename "${apps[0]}" .app)}.app"
ditto "${apps[0]}" "$app"

# One signature covers the app as it is built today: a single binary, its
# packages linked in. Anything that is code of its own needs signing first,
# and an extension its own entitlements and profile, so stop instead of
# shipping it with the signature it came with.
for nested in Frameworks PlugIns XPCServices Helpers Library; do
    [ -z "$(ls -A "$app/Contents/$nested" 2>/dev/null)" ] ||
        die "The app has Contents/$nested, which this script doesn't sign. Teach it to before uploading."
done

# The profile says which app and team it is for. The app must be that one:
# App Store Connect takes a mismatch and then drops the build while processing.
security cms -D -i "$profile" -o "$work/profile.plist" 2>/dev/null ||
    die "Couldn't read the provisioning profile."
app_id=$(value "$work/profile.plist" Entitlements:com.apple.application-identifier) ||
    die "The profile names no application: it isn't a Mac App Store profile."
team=$(value "$work/profile.plist" Entitlements:com.apple.developer.team-identifier) ||
    die "The profile names no team."
bundle_id=$(value "$app/Contents/Info.plist" CFBundleIdentifier)
[ "$app_id" = "$team.$bundle_id" ] ||
    die "The profile is for ${app_id#*.}, and the app is $bundle_id."

# What the app was built to ask for, read back from its signature rather than
# from the project so it is what Xcode resolved. TestFlight needs the two
# identifiers on top: without them the build uploads and can't be installed.
codesign -d --entitlements - --xml "$app" > "$work/entitlements.plist" 2>/dev/null || true
[ "$(value "$work/entitlements.plist" com.apple.security.app-sandbox)" = true ] ||
    die "The archived app isn't sandboxed, which the Mac App Store requires."
edit() { /usr/libexec/PlistBuddy -c "$1" "$work/entitlements.plist" > /dev/null 2>&1; }
edit "Delete :com.apple.security.get-task-allow" || true
edit "Delete :com.apple.application-identifier" || true
edit "Delete :com.apple.developer.team-identifier" || true
edit "Add :com.apple.application-identifier string $app_id"
edit "Add :com.apple.developer.team-identifier string $team"

cp "$profile" "$app/Contents/embedded.provisionprofile"
codesign --force --sign "$app_identity" --entitlements "$work/entitlements.plist" "$app"
codesign --verify --strict --deep "$app"

mkdir -p "$(dirname "$out")"
rm -f "$out"
productbuild --component "$app" /Applications --sign "$installer_identity" "$out"
pkgutil --check-signature "$out" > /dev/null ||
    die "The package's signature doesn't check out."
echo "Packaged $(basename "$app") ($bundle_id) as $out." >&2
