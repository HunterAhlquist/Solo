#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
classify() { bash "$root/build/release-channel.sh" "$@"; }
test "$(classify server server-v2.0.0)" = $'version=2.0.0\nprerelease=false'
test "$(classify desktop desktop-v2.0.0-rc.1)" = $'version=2.0.0-rc.1\nprerelease=true'
test "$(classify server preview-v1.10.0-design-docs.1)" = $'version=1.10.0-design-docs.1\nprerelease=true'
test "$(classify desktop preview-v1.10.0-design-docs.1)" = $'version=1.10.0-design-docs.1\nprerelease=true'
for tag in preview-v1.10.0 preview-v01.0.0-rc.1 preview-v1.0.0-rc.01 preview-v1.0.0- preview-v1.0.0+meta desktop-v1.0.0; do
    if classify server "$tag" >/dev/null 2>&1; then
        echo "Incorrectly accepted $tag" >&2; exit 1
    fi
done
echo "Release channel checks passed."
