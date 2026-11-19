#!/usr/bin/env bash
# build/prune-prereleases.sh deletes GitHub releases, which cannot be taken
# back: what it removes and, more to the point, what it never does is driven
# here against a stub `gh`.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The releases are lines of "createdAt<TAB>tag<TAB>kind" in $RELEASES, where
# kind is prerelease, stable or draft (a draft prerelease). `gh release list`
# answers as the script's --jq would have it; `gh release delete` takes the
# line out and writes the tag to $DELETED. $LIST_FAILS and $DELETE_FAILS make
# either fail.
mkdir -p "$work/bin"
cat > "$work/bin/gh" <<'STUB'
#!/usr/bin/env bash
[ "$1" = release ] || exit 2
case "$2" in
    list)
        [ -z "${LIST_FAILS:-}" ] || { echo "error connecting to api.github.com" >&2; exit 1; }
        awk -F'\t' '$3 == "prerelease" { print $1 "\t" $2 }' "$RELEASES"
        ;;
    delete)
        case " $* " in *" --cleanup-tag "*) echo "the tag must be kept" >&2; exit 3;; esac
        case " $* " in *" --yes "*) ;; *) echo "would have asked" >&2; exit 4;; esac
        [ "${DELETE_FAILS:-}" != "$3" ] || { echo "HTTP 500" >&2; exit 1; }
        echo "$3" >> "$DELETED"
        awk -F'\t' -v tag="$3" '$2 != tag' "$RELEASES" > "$RELEASES.next" && mv "$RELEASES.next" "$RELEASES"
        ;;
    *) exit 2 ;;
esac
STUB
chmod +x "$work/bin/gh"
export PATH="$work/bin:$PATH" RELEASES="$work/releases" DELETED="$work/deleted" GITHUB_REPOSITORY=owner/repo

seed() {
    : > "$DELETED"
    printf '%s\t%s\t%s\n' \
        2026-10-01T10:00:00Z v1.17.0 stable \
        2026-10-23T01:27:00Z preview-v1.17.0-design-docs.1 prerelease \
        2026-11-04T09:00:00Z v1.20.0 stable \
        2026-11-18T21:01:20Z preview-v1.21.0-mobile.20 prerelease \
        2026-11-19T00:03:05Z preview-v1.21.0-mobile.21 prerelease \
        2026-11-19T00:10:00Z preview-v1.21.0-notes.1 draft \
        2026-11-19T00:54:07Z preview-v1.21.0-mobile.22 prerelease \
        2026-11-19T20:04:39Z preview-v1.21.0-mobile.23 prerelease \
        > "$RELEASES"
}
prune() { bash "$root/build/prune-prereleases.sh" "$@"; }
deleted() { sort "$DELETED" | tr '\n' ' '; }
left() { cut -f2 "$RELEASES" | sort | tr '\n' ' '; }
expect() {
    if [ "$2" != "$3" ]; then
        echo "FAIL: $1" >&2; echo "  expected: $3" >&2; echo "  got:      $2" >&2; exit 1
    fi
}

# A new prerelease takes down every prerelease before it, of whatever line, and
# nothing else: not a stable release, not a draft, not itself.
seed
prune preview-v1.21.0-mobile.23 true > /dev/null
expect "the newest prerelease removes the ones before it" "$(deleted)" \
    "preview-v1.17.0-design-docs.1 preview-v1.21.0-mobile.20 preview-v1.21.0-mobile.21 preview-v1.21.0-mobile.22 "
expect "stable releases, drafts and the new prerelease are left" "$(left)" \
    "preview-v1.21.0-mobile.23 preview-v1.21.0-notes.1 v1.17.0 v1.20.0 "

# Run again, as the second workflow of the same tag does: nothing more to do.
: > "$DELETED"
prune preview-v1.21.0-mobile.23 true > /dev/null
expect "a second run removes nothing" "$(deleted)" ""

# A preview whose workflow finishes after a later tag's has been published must
# not take the later one down.
seed
prune preview-v1.21.0-mobile.21 true > /dev/null
expect "an older tag removes only what is older than it" "$(deleted)" \
    "preview-v1.17.0-design-docs.1 preview-v1.21.0-mobile.20 "
expect "what was tagged after it is kept" "$(left)" \
    "preview-v1.21.0-mobile.21 preview-v1.21.0-mobile.22 preview-v1.21.0-mobile.23 preview-v1.21.0-notes.1 v1.17.0 v1.20.0 "

# A stable release removes nothing, though prereleases older than it exist.
seed
prune v1.20.0 false > /dev/null
expect "a stable release leaves prereleases alone" "$(deleted)" ""

# A tag with no prerelease of its own has nothing to say which are older.
seed
out="$(prune preview-v1.21.0-mobile.99 true)"
expect "an unpublished tag removes nothing" "$(deleted)" ""
grep -q '::warning::' <<< "$out" || { echo "FAIL: an unpublished tag must warn" >&2; exit 1; }

# An unreadable list removes nothing, warns, and does not fail the release.
seed
out="$(LIST_FAILS=1 prune preview-v1.21.0-mobile.23 true 2> /dev/null)"
expect "an unreadable list removes nothing" "$(deleted)" ""
grep -q '::warning::' <<< "$out" || { echo "FAIL: an unreadable list must warn" >&2; exit 1; }

# One that cannot be removed is said, the rest still go, and the job passes.
seed
out="$(DELETE_FAILS=preview-v1.21.0-mobile.21 prune preview-v1.21.0-mobile.23 true 2> /dev/null)"
expect "a failed removal does not stop the others" "$(deleted)" \
    "preview-v1.17.0-design-docs.1 preview-v1.21.0-mobile.20 preview-v1.21.0-mobile.22 "
grep -q '::warning::.*mobile.21' <<< "$out" || { echo "FAIL: a failed removal must warn" >&2; exit 1; }

# No arguments is a mistake in the workflow, not something to pass quietly.
if prune > /dev/null 2>&1; then echo "FAIL: missing arguments must fail" >&2; exit 1; fi

echo "Prerelease pruning checks passed."
