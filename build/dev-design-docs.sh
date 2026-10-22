#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export SOLO_DEVELOPMENT_PACKAGE="$root/artifacts/design-docs-dev"
# Replacing this generated package removes any stale files from prior builds.
rm -rf "$SOLO_DEVELOPMENT_PACKAGE"
"$root/build/pack-design-docs-extension.sh"
printf '\nDevelopment package: %s\nSet Extensions__DevelopmentDirectories__0 to this path and restart Solo.\n' "$SOLO_DEVELOPMENT_PACKAGE"
