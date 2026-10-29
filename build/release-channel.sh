#!/usr/bin/env bash
# One classification for image tags, branch guards, installer versions, the
# desktop updater feed and the GitHub release.
#
# There is one release series. A `v<version>` tag releases the whole train —
# server image, desktop installers, extension packages — as a single GitHub
# release, so exactly one release per train can hold "latest" and nothing gets
# buried behind a sibling. `preview-v<version>` is the same train published as a
# prerelease, which leaves every stable channel where it is.
#
# The per-component `server-v*` and `desktop-v*` series are retired and are
# rejected here: they are what produced two releases for one commit.
set -euo pipefail

tag=${1:?Usage: release-channel.sh <tag>}
case "$tag" in
    preview-v*) version=${tag#preview-v}; preview=true ;;
    v*)         version=${tag#v};         preview=false ;;
    *) echo "Expected v<version> or preview-v<version>." >&2; exit 1 ;;
esac

# Build metadata is not used in Docker tags. Each preview uses a distinct
# SemVer prerelease so installs and rollbacks have an exact image tag.
if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
    echo "Invalid release version: $version" >&2; exit 1
fi
prerelease=false
if [[ "$version" == *-* ]]; then
    prerelease=true
    suffix=${version#*-}
    IFS=. read -r -a identifiers <<< "$suffix"
    for identifier in "${identifiers[@]}"; do
        if [[ "$identifier" =~ ^[0-9]+$ && "$identifier" != 0 && "$identifier" == 0* ]]; then
            echo "Numeric prerelease identifiers cannot have leading zeros." >&2; exit 1
        fi
    done
fi
if [ "$preview" = true ] && [ "$prerelease" != true ]; then
    echo "Preview tags require a prerelease suffix, for example preview-v1.18.0-design-docs.1." >&2; exit 1
fi
preview_channel=preview
package_label=
if [[ "$version" =~ -design-docs-([a-z][a-z0-9-]*)\.[0-9]+$ ]]; then
    package_label=${BASH_REMATCH[1]}
    preview_channel="preview-$package_label"
fi
printf 'version=%s\nprerelease=%s\npreview_channel=%s\npackage_label=%s\n' "$version" "$prerelease" "$preview_channel" "$package_label"
