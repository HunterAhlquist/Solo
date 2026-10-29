#!/usr/bin/env bash
# build/release-baseline.sh decides which manifest a release's merge starts
# from. Both ways of getting it wrong publish a broken release — a manifest
# contradicting the assets beside it, or one that silently dropped every
# component this train did not rebuild — so every branch is driven here against
# a stub `gh`.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Releases are directories under $RELEASES. $API_FAILS makes `gh api` fail the
# way a network error does (no "HTTP 404"), and $DOWNLOAD_FAILS makes a download
# of an asset that is definitely there fail. Those two are the cases that matter.
mkdir -p "$work/bin"
cat > "$work/bin/gh" <<'STUB'
#!/usr/bin/env bash
case "$1" in
    api)
        if [ -n "${API_FAILS:-}" ]; then
            echo "error connecting to api.github.com" >&2; exit 1
        fi
        tag=${2##*/}
        if [ ! -d "$RELEASES/$tag" ]; then
            echo "gh: Not Found (HTTP 404)" >&2; exit 1
        fi
        shopt -s nullglob
        for f in "$RELEASES/$tag"/*; do basename "$f"; done
        ;;
    release)
        [ "$2" = "download" ] || exit 0
        if [ -n "${DOWNLOAD_FAILS:-}" ]; then echo "transient" >&2; exit 1; fi
        tag=$3; pattern=""; dir="."; shift 3
        while [ $# -gt 0 ]; do
            case "$1" in -p) pattern=$2; shift 2;; -D) dir=$2; shift 2;; *) shift;; esac
        done
        shopt -s nullglob
        found=()
        for f in "$RELEASES/$tag"/$pattern; do [ -f "$f" ] && found+=("$f"); done
        [ ${#found[@]} -eq 0 ] && { echo "no asset" >&2; exit 1; }
        mkdir -p "$dir" && cp "${found[@]}" "$dir/"
        ;;
esac
STUB
chmod +x "$work/bin/gh"
export PATH="$work/bin:$PATH" RELEASES="$work/releases" GITHUB_REPOSITORY=owner/repo

mkdir -p "$RELEASES/v1.17.0" "$RELEASES/v1.18.0-empty" "$RELEASES/v1.19.0"
echo '{"from":"previous"}' > "$RELEASES/v1.17.0/release-manifest.json"
echo 'installer' > "$RELEASES/v1.18.0-empty/Solo-1.18.0-arm64.dmg"
echo '{"from":"this release"}' > "$RELEASES/v1.19.0/release-manifest.json"

baseline() { bash "$root/build/release-baseline.sh" "$@" 2>/dev/null; }
refuses() {
    local label=$1; shift
    if "$@" >/dev/null 2>&1; then echo "FAIL: $label" >&2; exit 1; fi
}

# No release for this tag yet: the previous release's manifest is the baseline.
out="$(baseline v1.18.0 v1.17.0 "$work/a")"
test "$out" = "$work/a/release-manifest.json"
grep -q '"from":"previous"' "$out"

# The other workflow got here first, so its manifest wins: falling back would
# overwrite the entries it just wrote with older ones.
out="$(baseline v1.19.0 v1.17.0 "$work/b")"
grep -q '"from":"this release"' "$out"

# The release exists but carries no manifest yet, so the previous one is still
# the right baseline.
out="$(baseline v1.18.0-empty v1.17.0 "$work/c")"
grep -q '"from":"previous"' "$out"

# Genuinely nothing to start from. Not an error: this is the first release.
out="$(baseline v1.20.0 '' "$work/d")"
test -z "$out"
out="$(baseline v1.20.0 v1.99.0 "$work/e")"
test -z "$out"

# The asset is listed but the download fails. Degrading to the previous
# release's manifest here would publish one contradicting this release's assets.
refuses "a failed download of a manifest that exists must not fall back" \
    env DOWNLOAD_FAILS=1 bash "$root/build/release-baseline.sh" v1.19.0 v1.17.0 "$work/f"

# The API is unreachable, so whether either release carries a manifest is
# unknown. Returning "no baseline" would publish a manifest that dropped every
# component this train did not rebuild.
refuses "an unreadable release must not be treated as having no manifest" \
    env API_FAILS=1 bash "$root/build/release-baseline.sh" v1.18.0 v1.17.0 "$work/g"

# A 404 is the only thing that establishes absence, and it must still work while
# the rest of the API is fine.
out="$(baseline v1.404.0 v1.17.0 "$work/h")"
grep -q '"from":"previous"' "$out"

# GITHUB_REPOSITORY is required: without it the script would read nothing and
# wrongly conclude there is no baseline.
refuses "a missing GITHUB_REPOSITORY must not read as an absent baseline" \
    env -u GITHUB_REPOSITORY bash "$root/build/release-baseline.sh" v1.18.0 v1.17.0 "$work/i"

echo "Release baseline checks passed."
