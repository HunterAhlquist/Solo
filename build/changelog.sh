#!/usr/bin/env bash
# The release notes come from CHANGELOG.md. Changes are written under a
# `## Latest` header as they land, because the version is not known until the
# tag is pushed.
#
#   notes  prints the Latest section's body, which the release jobs use as the
#          GitHub release's notes.
#   sync   dates the section once it has shipped: when the newest stable `v*`
#          tag is newer than the newest released section, Latest is renamed to
#          that tag and its date and a fresh Latest goes on top. Run it before
#          adding an entry. It is a step in the next change rather than a
#          commit CI pushes at release time because main is protected, and the
#          workflow token can't be put on a bypass list.
#
# A `##` header ends a section; `###` and deeper belong to it.
set -euo pipefail

file=${CHANGELOG:-CHANGELOG.md}
usage='Usage: changelog.sh notes | sync'

case "${1:-}" in
    notes)
        if ! grep -qx '## Latest' "$file"; then
            echo "${file} has no '## Latest' section." >&2
            exit 1
        fi
        # The blank lines around the section are dropped (the substitution
        # strips the trailing ones); the body itself is printed as written.
        body=$(awk '/^## Latest$/ {inside = 1; next} /^## / {inside = 0} inside' "$file" | sed '/./,$!d')
        if [ -z "$(tr -d '[:space:]' <<< "$body")" ]; then
            echo "${file}'s Latest section is empty." >&2
            exit 1
        fi
        printf '%s\n' "$body"
        ;;
    sync)
        # Prereleases carry a `-` suffix and don't consume the section: the
        # same changes ship again as stable.
        tag=$(git tag --list 'v*' | grep -v -- '-' | sort -V | tail -1 || true)
        released=$(sed -n 's/^## \(v[^ ]*\) (.*$/\1/p' "$file" | head -1)
        updated=$(mktemp)

        if [ -n "$tag" ] && [ "$tag" != "$released" ] &&
           [ "$(printf '%s\n' "$tag" "$released" | sort -V | tail -1)" = "$tag" ]; then
            date=$(TZ=UTC git for-each-ref --format='%(creatordate:format-local:%Y-%m-%d)' "refs/tags/${tag}")
            if grep -qx '## Latest' "$file"; then
                awk -v header="## ${tag} (${date})" '
                    !done && /^## Latest$/ {print "## Latest\n\n" header; done = 1; next}
                    {print}
                ' "$file" > "$updated"
                echo "Dated the Latest section as ${tag} (${date}) and started a new one."
            else
                # Nothing was tracked for that release. It still gets a header,
                # or the next sync would date the new Latest as this tag.
                awk -v header="## ${tag} (${date})" '
                    !done && /^## / {print "## Latest\n\n" header "\n\nNo changelog was kept for this release.\n"; done = 1}
                    {print}
                ' "$file" > "$updated"
                echo "${tag} shipped with no Latest section; started a new one."
            fi
        elif ! grep -qx '## Latest' "$file"; then
            awk '!done && /^## / {print "## Latest\n"; done = 1} {print}' "$file" > "$updated"
            echo "Added a Latest section."
        else
            echo "Latest is current: the newest tag, ${tag:-none}, already has its section."
            rm "$updated"
            exit 0
        fi
        mv "$updated" "$file"
        ;;
    *) echo "$usage" >&2; exit 1 ;;
esac
