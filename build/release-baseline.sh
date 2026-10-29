#!/usr/bin/env bash
# Resolves the baseline for a release's manifest merge. Prints the path it
# wrote to stdout, or nothing when there genuinely is no baseline; diagnostics
# go to stderr so the caller can capture the path alone.
#
#   release-baseline.sh <tag> <previous-tag-or-empty> <directory>
#
# Both release workflows merge into the one release-manifest.json on a tag's
# release, so the baseline is whichever already exists:
#
#   * the manifest on this tag's release, if the other workflow got there first;
#   * else the previous release's, so a component this train did not rebuild
#     keeps its version and its download URL.
#
# Everything here exists to avoid *guessing* which case we are in, because both
# wrong guesses publish a broken release:
#
#   * mistaking "this release's manifest is unreadable" for "it has none yet"
#     falls through to the previous release and overwrites the entries the other
#     workflow just wrote with older ones — a manifest contradicting the assets
#     beside it;
#   * mistaking "the previous manifest is unreadable" for "there is no baseline"
#     publishes a manifest describing only what this train rebuilt, silently
#     dropping the desktop app and every unchanged extension from the newest
#     release's manifest, which is the one every updater reads.
#
# So absence is established from a 404 and nothing else. Any other failure is
# fatal: re-running the job is cheap, and a truncated manifest is not.
set -euo pipefail

tag=${1:?Usage: release-baseline.sh <tag> <previous> <dir>}
previous=${2-}
dir=${3:?Usage: release-baseline.sh <tag> <previous> <dir>}
repository=${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must name the repository to read releases from}
manifest=release-manifest.json

mkdir -p "$dir"
errors="$dir/.gh-error"
listing="$dir/.gh-assets"

# The asset names on a release, into $listing. Returns 1 when the release does
# not exist and 2 when that could not be established. Deliberately writes to a
# file rather than stdout: read through `$(…)` this would run in a subshell,
# where an `exit` on the fatal path would end only the subshell and read back as
# "no such release" — the very guess this script exists to refuse.
release_assets() {
    local candidate=$1
    if gh api "repos/${repository}/releases/tags/${candidate}" \
        --jq '[.assets[].name] | join("\n")' > "$listing" 2>"$errors"; then
        return 0
    fi
    if grep -q 'HTTP 404' "$errors"; then
        return 1
    fi
    cat "$errors" >&2
    echo "::error::Could not read release ${candidate}, so whether it carries a ${manifest} is unknown." \
         "Refusing to guess: publishing the wrong baseline would either overwrite another job's entries" \
         "or drop every component this train did not rebuild. Re-run this job." >&2
    return 2
}

for candidate in "$tag" "$previous"; do
    [ -n "$candidate" ] || continue

    status=0
    release_assets "$candidate" || status=$?
    case "$status" in
        0) ;;
        1) echo "No release ${candidate}." >&2; continue ;;
        *) exit 1 ;;
    esac

    if ! grep -qxF "$manifest" "$listing"; then
        echo "Release ${candidate} carries no ${manifest}." >&2
        continue
    fi

    # The asset is known to be there, so a failed download is a real failure
    # rather than a reason to look elsewhere.
    gh release download "$candidate" -p "$manifest" -D "$dir"
    if [ "$candidate" = "$tag" ]; then
        echo "Merging into the ${manifest} already on ${tag}." >&2
    else
        echo "Carrying ${manifest} forward from ${candidate}." >&2
    fi
    rm -f "$errors" "$listing"
    echo "$dir/$manifest"
    exit 0
done

rm -f "$errors" "$listing"
echo "::notice::No baseline manifest; this release's manifest describes only what it built." >&2
