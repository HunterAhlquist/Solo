#!/usr/bin/env bash
#
# Copies into the documentation site the files it borrows from the rest of the
# repository:
#
#   docs/images/screenshots/              -> site/assets/screenshots/
#   src/Solo.Client/wwwroot/favicon.svg   -> site/assets/favicon.svg
#
# The README shows the same screenshots and the app serves the same icon, so
# each keeps one source. Both destinations are in .gitignore. The Pages
# workflow runs this before its Jekyll build, and build/preview-site.sh before
# a local one.
#
# Usage: build/stage-site-assets.sh

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
assets="$root/site/assets"

# Replaced whole, so a screenshot deleted from docs/ does not linger here.
rm -rf "$assets/screenshots"
mkdir -p "$assets/screenshots"
cp "$root"/docs/images/screenshots/*.png "$assets/screenshots/"

cp "$root/src/Solo.Client/wwwroot/favicon.svg" "$assets/favicon.svg"

echo "Staged $(find "$assets/screenshots" -name '*.png' | wc -l | tr -d ' ') screenshots and the icon into site/assets."
