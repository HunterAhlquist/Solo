#!/usr/bin/env bash
# Whether nuget.org is behind the version in this checkout for one package:
# prints `behind=true` when nothing it lists is that version or newer, and
# `behind=false` when the version is already there or something newer is.
# A `v*` release reads this to publish only what the train actually moved.
#
# NUGET_INDEX_JSON stands in for the fetch in tests.
set -euo pipefail

id=${1:?Usage: nuget-behind.sh <package-id> <version>}
version=${2:?Usage: nuget-behind.sh <package-id> <version>}
id=$(tr '[:upper:]' '[:lower:]' <<< "$id")

if [ -n "${NUGET_INDEX_JSON+x}" ]; then
    index=$NUGET_INDEX_JSON
else
    # 404 is a package nuget.org has never seen; anything else is a real failure.
    body=$(mktemp)
    status=$(curl -sS -o "$body" -w '%{http_code}' \
        "https://api.nuget.org/v3-flatcontainer/${id}/index.json")
    case "$status" in
        200) index=$(cat "$body") ;;
        404) index='{"versions":[]}' ;;
        *) echo "nuget.org answered ${status} for ${id}." >&2; exit 1 ;;
    esac
fi

# The flat index lists versions lower-cased, oldest first, prereleases included.
mapfile -t listed < <(grep -o '"[^"]*"' <<< "$index" | tr -d '"' | grep -v '^versions$' || true)

lower=$(tr '[:upper:]' '[:lower:]' <<< "$version")
for v in "${listed[@]}"; do
    if [ "$v" = "$lower" ]; then
        echo "${id} ${version} is already on nuget.org." >&2
        echo "behind=false"
        exit 0
    fi
done

newest=$(printf '%s\n' "${listed[@]}" "$lower" | sort -V | tail -1)
if [ "$newest" != "$lower" ]; then
    echo "::warning::${id} ${version} is older than ${newest} on nuget.org; not publishing a version behind the feed." >&2
    echo "behind=false"
    exit 0
fi

echo "nuget.org has no ${id} ${version} yet." >&2
echo "behind=true"
