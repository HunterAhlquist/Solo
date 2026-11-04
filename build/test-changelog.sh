#!/usr/bin/env bash
# Exercises build/changelog.sh against small changelogs.
set -euo pipefail

cd "$(dirname "$0")/.."

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

cat > "$tmp/CHANGELOG.md" <<'MD'
# Changelog

## Latest

### Server
- A change.

### Extensions

#### Design Docs (1.5.3)
- Another.

## v1.19.9 (2026-11-03)

### Desktop
- Old.
MD

got=$(CHANGELOG="$tmp/CHANGELOG.md" bash build/changelog.sh notes)
want=$'### Server\n- A change.\n\n### Extensions\n\n#### Design Docs (1.5.3)\n- Another.'
[ "$got" = "$want" ] || fail "notes printed:
$got"

CHANGELOG="$tmp/CHANGELOG.md" bash build/changelog.sh release v1.20.0 2026-11-04
grep -qx '## v1.20.0 (2026-11-04)' "$tmp/CHANGELOG.md" || fail "release did not rename the header"
grep -qx '## v1.19.9 (2026-11-03)' "$tmp/CHANGELOG.md" || fail "release touched an older section"
! grep -qx '## Latest' "$tmp/CHANGELOG.md" || fail "release left the Latest header in place"

# Without a Latest section both commands refuse, so a release falls back to
# generated notes and a re-run of the rename cannot rename the wrong section.
! CHANGELOG="$tmp/CHANGELOG.md" bash build/changelog.sh notes 2>/dev/null || fail "notes accepted a changelog with no Latest section"
! CHANGELOG="$tmp/CHANGELOG.md" bash build/changelog.sh release v1.20.1 2>/dev/null || fail "release accepted a changelog with no Latest section"

# A Latest section with nothing in it is not release notes.
printf '# Changelog\n\n## Latest\n\n## v1.0.0 (2026-01-01)\n- Old.\n' > "$tmp/CHANGELOG.md"
! CHANGELOG="$tmp/CHANGELOG.md" bash build/changelog.sh notes 2>/dev/null || fail "notes accepted an empty Latest section"

# A tag that already has a section is not renamed onto a second time.
printf '# Changelog\n\n## Latest\n- New.\n\n## v1.0.0 (2026-01-01)\n- Old.\n' > "$tmp/CHANGELOG.md"
! CHANGELOG="$tmp/CHANGELOG.md" bash build/changelog.sh release v1.0.0 2>/dev/null || fail "release renamed Latest onto a tag that already has a section"
grep -qx '## Latest' "$tmp/CHANGELOG.md" || fail "a refused release still changed the file"

echo "changelog: ok"
