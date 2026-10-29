#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
classify() { bash "$root/build/release-channel.sh" "$@"; }
test "$(classify v2.0.0)" = $'version=2.0.0\nprerelease=false\npreview_channel=preview\npackage_label='
test "$(classify v2.0.0-rc.1)" = $'version=2.0.0-rc.1\nprerelease=true\npreview_channel=preview\npackage_label='
test "$(classify preview-v1.18.0-design-docs.1)" = $'version=1.18.0-design-docs.1\nprerelease=true\npreview_channel=preview\npackage_label='
test "$(classify preview-v1.18.0-design-docs-codex.3)" = $'version=1.18.0-design-docs-codex.3\nprerelease=true\npreview_channel=preview-codex\npackage_label=codex'
test "$(classify preview-v1.18.0-design-docs-claude.1)" = $'version=1.18.0-design-docs-claude.1\nprerelease=true\npreview_channel=preview-claude\npackage_label=claude'
# A preview tag needs a prerelease suffix; the retired per-component series and
# the SDK's own series must not be mistaken for a release of the whole train.
for tag in preview-v1.18.0 preview-v01.0.0-rc.1 preview-v1.0.0-rc.01 preview-v1.0.0- preview-v1.0.0+meta \
           server-v1.0.0 desktop-v1.0.0 sdk-v1.11.1 templates-v1.11.1 v1.0 1.0.0; do
    if classify "$tag" >/dev/null 2>&1; then
        echo "Incorrectly accepted $tag" >&2; exit 1
    fi
done
echo "Release channel checks passed."
