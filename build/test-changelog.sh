#!/usr/bin/env bash
# Exercises build/changelog.sh against small changelogs and a throwaway repo.
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/changelog.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
cd "$tmp"

fail() { echo "FAIL: $*" >&2; exit 1; }
run() { CHANGELOG="$tmp/CHANGELOG.md" bash "$script" "$@"; }

git init -q
git -c user.name=t -c user.email=t@t commit -q --allow-empty -m one
tag() { GIT_COMMITTER_DATE="$2T12:00:00Z" git -c user.name=t -c user.email=t@t tag -a "$1" -m "$1"; }
tag v1.19.9 2026-11-03

cat > CHANGELOG.md <<'MD'
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

got=$(run notes)
want=$'### Server\n- A change.\n\n### Extensions\n\n#### Design Docs (1.5.3)\n- Another.'
[ "$got" = "$want" ] || fail "notes printed:
$got"

# The newest tag already has its section, so nothing moves.
before=$(cat CHANGELOG.md)
run sync > /dev/null
[ "$(cat CHANGELOG.md)" = "$before" ] || fail "sync changed a current changelog"

# A prerelease doesn't consume the section.
tag v1.20.0-rc.1 2026-11-04
run sync > /dev/null
[ "$(cat CHANGELOG.md)" = "$before" ] || fail "sync dated the section for a prerelease"

# A newer stable tag dates the section with the tag's own date and opens a new one.
tag v1.20.0 2026-11-05
run sync > /dev/null
[ "$(grep '^## ' CHANGELOG.md)" = $'## Latest\n## v1.20.0 (2026-11-05)\n## v1.19.9 (2026-11-03)' ] ||
    fail "sync produced:
$(grep '^## ' CHANGELOG.md)"
grep -A2 -x '## v1.20.0 (2026-11-05)' CHANGELOG.md | grep -qx '### Server' || fail "the dated section lost its body"
! run notes 2>/dev/null || fail "notes accepted an empty Latest section"

# Run again, it is a no-op; numeric, not lexical, ordering of tags.
run sync > /dev/null
[ "$(grep -c '^## ' CHANGELOG.md)" = 3 ] || fail "a second sync changed the file"
tag v1.9.0 2026-11-06
run sync > /dev/null
[ "$(grep -c '^## ' CHANGELOG.md)" = 3 ] || fail "sync took v1.9.0 for newer than v1.20.0"

# A missing Latest header is the cue to start tracking again.
sed -i '/^## Latest$/,+1d' CHANGELOG.md
run sync > /dev/null
[ "$(grep '^## ' CHANGELOG.md | head -1)" = '## Latest' ] || fail "sync did not restore a missing Latest header"
! run notes 2>/dev/null || fail "notes accepted a changelog with an empty Latest section"

# A release that shipped with no Latest section still gets its header, so the
# next sync doesn't date the new section as that release.
sed -i '/^## Latest$/,+1d' CHANGELOG.md
tag v1.21.0 2026-11-07
run sync > /dev/null
[ "$(grep '^## ' CHANGELOG.md | head -2)" = $'## Latest\n## v1.21.0 (2026-11-07)' ] ||
    fail "sync after an untracked release produced:
$(grep '^## ' CHANGELOG.md)"
sed -i 's/^## Latest$/## Latest\n\n- New./' CHANGELOG.md
run sync > /dev/null
[ "$(run notes)" = '- New.' ] || fail "a second sync after an untracked release moved the new entries"

echo "changelog: ok"
