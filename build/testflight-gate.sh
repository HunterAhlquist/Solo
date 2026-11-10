#!/usr/bin/env bash
# Decides whether a TestFlight job has an app to upload, by comparing what goes
# into the app against the commit of the last build that reached TestFlight.
#
#   testflight-gate.sh <ios|mac>
#
# Prints `upload=true|false` and `baseline=<sha or empty>` for $GITHUB_OUTPUT,
# and nothing else, and says why in the job summary. Everything else, the
# skip notice included, goes to stderr.
#
# Most preview tags change the server or an extension and leave both apps
# alone: both apps load the client their instance serves, so neither carries
# any of the client or the server. A build
# that changes nothing still costs a macOS runner, a processing slot at Apple
# and a TestFlight notification to every tester.
#
# The baseline is the last run of the same workflow whose Upload step
# succeeded, not the previous tag. A tag whose upload failed, or which this
# gate skipped, says nothing about what testers have installed; diffing against
# it would let an app change that never reached TestFlight be skipped again on
# the next tag. The baseline may sit on another branch, and that is still the
# right comparison: it is the build testers have.
#
# Whenever the answer is not certain — a manual run, no earlier upload, the
# API unreadable, the baseline commit missing — it uploads. An unneeded upload
# costs a few minutes; a wrongly skipped one leaves testers on a stale app with
# nothing to say so.
set -euo pipefail

app=${1:?Usage: testflight-gate.sh <ios|mac>}
case "$app" in
    # The iOS app: the Capacitor project, the icon sources it stages, and the
    # desktop script that staging reuses. The client is served by the
    # instance, so src/Solo.Client is deliberately absent. Both apps count
    # release-channel.sh, which decides the version and preview identity
    # stamped into them; write-asc-key.sh only handles the key, so neither does.
    ios)
        name="iOS app"
        workflow=release-ios.yml
        paths=(
            src/Solo.Mobile
            ":(exclude)src/Solo.Mobile/README.md"
            Icons
            src/Solo.Desktop/scripts/stage-icon.mjs
            build/release-channel.sh
            .github/workflows/release-ios.yml
        )
        ;;
    # The Mac App Store build: the Electron shell and the icon sources, the
    # same inputs release-desktop.yml packages. The client isn't among them:
    # the shell proxies the one each instance serves.
    mac)
        name="Mac app"
        workflow=release-mac-store.yml
        paths=(
            src/Solo.Desktop
            ":(exclude)src/Solo.Desktop/README.md"
            Icons
            build/release-channel.sh
            .github/workflows/release-mac-store.yml
        )
        ;;
    *) echo "Expected ios or mac, got '$app'." >&2; exit 2 ;;
esac

repository=${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must name the repository}
current_run=${GITHUB_RUN_ID:-}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}

decide() {
    local upload=$1 baseline=$2 reason=$3
    if [ "$upload" = true ]; then
        echo "$reason" >&2
        printf '### TestFlight upload\n\n%s\n' "$reason" >> "$summary"
    else
        # stderr: stdout is the caller's $GITHUB_OUTPUT, and the runner reads
        # workflow commands from either stream.
        echo "::notice title=TestFlight upload skipped::$reason" >&2
        printf '### TestFlight upload skipped\n\n%s\n' "$reason" >> "$summary"
    fi
    echo "upload=$upload"
    echo "baseline=$baseline"
    exit 0
}

if [ "${GITHUB_EVENT_NAME:-}" = workflow_dispatch ]; then
    decide true "" "Started by hand, so it uploads whatever changed."
fi

if ! runs=$(gh api "repos/$repository/actions/workflows/$workflow/runs?status=success&per_page=50" \
        --jq '.workflow_runs[] | "\(.id) \(.head_sha)"' 2>&1); then
    decide true "" "Couldn't list earlier $workflow runs, so it uploads anyway: $runs"
fi

baseline=""
while read -r id sha; do
    [ -n "$id" ] || continue
    [ "$id" = "$current_run" ] && continue
    if ! uploaded=$(gh api "repos/$repository/actions/runs/$id/jobs" \
            --jq '[.jobs[].steps[]? | select(.name == "Upload" and .conclusion == "success")] | length' 2>&1); then
        decide true "" "Couldn't read the steps of run $id, so it uploads anyway: $uploaded"
    fi
    if [ "${uploaded:-0}" -gt 0 ]; then
        baseline=$sha
        break
    fi
done <<< "$runs"

if [ -z "$baseline" ]; then
    decide true "" "No earlier $workflow run reached its Upload step, so this is the first build to upload."
fi

if ! git cat-file -e "$baseline^{commit}" 2>/dev/null; then
    git fetch --quiet --no-tags origin "$baseline" 2>/dev/null || true
fi
if ! git cat-file -e "$baseline^{commit}" 2>/dev/null; then
    decide true "$baseline" "The last uploaded build's commit ($baseline) isn't in this checkout, so it uploads anyway."
fi

short=$(git rev-parse --short "$baseline")
if git diff --quiet "$baseline" HEAD -- "${paths[@]}"; then
    decide false "$baseline" "Nothing in the $name changed since the build testers already have ($short), so there's nothing new to upload."
fi

changed=$(git diff --name-only "$baseline" HEAD -- "${paths[@]}")
count=$(wc -l <<< "$changed" | tr -d ' ')
echo "Changed since $short:" >&2
echo "$changed" >&2
decide true "$baseline" "The $name changed since the build testers have ($short): $count file(s), first $(head -n 1 <<< "$changed")."
