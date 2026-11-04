#!/usr/bin/env bash
# The release notes come from CHANGELOG.md. Changes are written under a
# `## Latest` header as they land, because the version is not known until the
# tag is pushed. `notes` prints that section's body, which the release jobs
# use as the GitHub release's notes; `release <tag> [YYYY-MM-DD]` renames the
# header to the tag and date once it has shipped, which the changelog job
# commits back to main so the next change starts a fresh section.
#
# A `##` header ends the section; `###` and deeper belong to it.
set -euo pipefail

file=${CHANGELOG:-CHANGELOG.md}
usage='Usage: changelog.sh notes | release <tag> [YYYY-MM-DD]'
command=${1:?$usage}

if ! grep -qx '## Latest' "$file"; then
    echo "${file} has no '## Latest' section." >&2
    exit 1
fi

case "$command" in
    notes)
        # The blank lines around the section are dropped (the substitution
        # strips the trailing ones); the body itself is printed as written.
        body=$(awk '/^## Latest$/ {inside = 1; next} /^## / {inside = 0} inside' "$file" | sed '/./,$!d')
        if [ -z "$(tr -d '[:space:]' <<< "$body")" ]; then
            echo "${file}'s Latest section is empty." >&2
            exit 1
        fi
        printf '%s\n' "$body"
        ;;
    release)
        tag=${2:?$usage}
        date=${3:-$(date -u +%Y-%m-%d)}
        if grep -q "^## ${tag} (" "$file"; then
            echo "${file} already has a section for ${tag}." >&2
            exit 1
        fi
        renamed=$(mktemp)
        awk -v header="## ${tag} (${date})" '
            !done && /^## Latest$/ {print header; done = 1; next}
            {print}
        ' "$file" > "$renamed"
        mv "$renamed" "$file"
        ;;
    *) echo "$usage" >&2; exit 1 ;;
esac
