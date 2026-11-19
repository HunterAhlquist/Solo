#!/usr/bin/env bash
# Keeps the release history to one prerelease.
#
# Every `preview-v*` tag publishes a GitHub prerelease, and they were never
# taken down: the releases page was mostly previews nobody was running, with
# the stable releases somewhere among them. So a prerelease, once published,
# takes down the ones before it.
#
#   bash build/prune-prereleases.sh <tag> <prerelease: true|false>
#
# What it leaves alone, and why:
#
#   * Stable releases, always. And a stable release removes nothing: the
#     prerelease that was newest stays until the next prerelease replaces it.
#   * Drafts. Someone is still writing those.
#   * Anything newer than <tag>. "Newer" is by when the tag was made — the
#     release's `createdAt` — and not by when it was published, so that of two
#     previews tagged minutes apart the later tag's is the one that survives,
#     whichever workflow finished first.
#   * The tags. Only the release and its assets go; `git checkout preview-v…`
#     still works, and the image a preview pushed to GHCR is not touched.
#
# Run it after the release has been written, from both release workflows: it is
# the same answer whichever gets there first. A preview that is still running
# when a later one is tagged can write its release again after this removed it;
# the next prerelease clears it away.
#
# It never fails the job. A prerelease left behind is clutter, which is what
# this is for and not worth a red release over: what it could not do, it says
# in a warning.
set -uo pipefail

tag=${1:?Usage: prune-prereleases.sh <tag> <prerelease: true|false>}
prerelease=${2:?Usage: prune-prereleases.sh <tag> <prerelease: true|false>}

if [ "$prerelease" != true ]; then
    echo "$tag is a stable release: prereleases are left as they are."
    exit 0
fi

repo=()
if [ -n "${GITHUB_REPOSITORY:-}" ]; then repo=(--repo "$GITHUB_REPOSITORY"); fi

# Tab-separated "createdAt<TAB>tag", prereleases only. An unreadable list
# removes nothing: guessing at it could take down the wrong release.
if ! listing=$(gh release list ${repo[@]+"${repo[@]}"} --limit 1000 \
        --json tagName,isPrerelease,isDraft,createdAt \
        --jq '.[] | select(.isPrerelease and (.isDraft | not)) | "\(.createdAt)\t\(.tagName)"'); then
    echo "::warning::The releases could not be listed, so no earlier prerelease was removed."
    exit 0
fi

mine=$(awk -F'\t' -v tag="$tag" '$2 == tag { print $1; exit }' <<< "$listing")
if [ -z "$mine" ]; then
    echo "::warning::$tag has no published prerelease to keep, so no earlier prerelease was removed."
    exit 0
fi

removed=0
while IFS=$'\t' read -r created other; do
    [ -n "$other" ] && [ "$other" != "$tag" ] || continue
    # ISO 8601 in UTC, so text order is time order.
    if [[ ! "$created" < "$mine" ]]; then
        echo "Keeping $other: it was tagged after $tag."
        continue
    fi
    if gh release delete "$other" ${repo[@]+"${repo[@]}"} --yes; then
        echo "Removed the prerelease $other."
        removed=$((removed + 1))
    else
        echo "::warning::The prerelease $other could not be removed; it is still on the releases page."
    fi
done <<< "$listing"

echo "$tag is the prerelease now; $removed earlier one(s) removed."
