#!/usr/bin/env bash
# One classification for image tags, branch guards, installer versions and GitHub releases.
set -euo pipefail

series=${1:?Usage: release-channel.sh server|desktop tag}
tag=${2:?Usage: release-channel.sh server|desktop tag}
case "$series" in server|desktop) ;; *) echo "Unknown release series: $series" >&2; exit 1 ;; esac
case "$tag" in
    preview-v*) version=${tag#preview-v}; preview=true ;;
    "$series-v"*) version=${tag#"$series-v"}; preview=false ;;
    *) echo "Expected ${series}-v<version> or preview-v<version>." >&2; exit 1 ;;
esac

# Build metadata isn't used in Docker tags. Require a numbered prerelease to
# make every preview install and rollback addressable by one immutable tag.
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
    echo "Preview tags require a prerelease suffix, for example preview-v1.10.0-design-docs.1." >&2; exit 1
fi
printf 'version=%s\nprerelease=%s\n' "$version" "$prerelease"
