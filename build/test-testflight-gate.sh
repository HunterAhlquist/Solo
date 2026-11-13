#!/usr/bin/env bash
# build/testflight-gate.sh decides whether a TestFlight job uploads. Skipping
# wrongly leaves testers on a stale app with nothing to say so, so every branch
# is driven here against a scratch repository and a stub `gh`.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gate="$root/build/testflight-gate.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Runs are lines of "<id> <sha>" in $RUNS (newest first, as the API returns
# them); a run whose Upload step succeeded has a file $UPLOADED/<id>. The stub
# answers with the API's JSON shape and applies the gate's own --jq filter, so
# the filters are tested too. Every run's other steps succeed and a run that
# didn't upload has its Upload step skipped, as a gated-off run does.
# $API_FAILS makes every call fail the way a network error does.
mkdir -p "$work/bin" "$work/uploaded"
cat > "$work/bin/gh" <<'STUB'
#!/usr/bin/env bash
[ "$1" = api ] || exit 0
if [ -n "${API_FAILS:-}" ]; then echo "error connecting to api.github.com" >&2; exit 1; fi
filter=.
[ "${3:-}" = --jq ] && filter=$4
case "$2" in
    */workflows/*/runs*)
        jq -Rn '{workflow_runs: [inputs | select(length > 0) | split(" ") | {id: (.[0] | tonumber), head_sha: .[1]}]}' < "$RUNS"
        ;;
    */runs/*/jobs)
        id=${2%/jobs}; id=${id##*/}
        upload=skipped; [ -f "$UPLOADED/$id" ] && upload=success
        jq -n --arg upload "$upload" '{jobs: [{steps: [
            {name: "Check what this run uploads", conclusion: "success"},
            {name: "Upload", conclusion: $upload},
            {name: "Remove the key", conclusion: "success"}]}]}'
        ;;
esac | jq -r "$filter"
STUB
chmod +x "$work/bin/gh"
export PATH="$work/bin:$PATH" RUNS="$work/runs" UPLOADED="$work/uploaded" \
    GITHUB_REPOSITORY=owner/repo GITHUB_EVENT_NAME=push GITHUB_RUN_ID=999 \
    GITHUB_STEP_SUMMARY="$work/summary"

repo="$work/repo"
git init --quiet "$repo"
cd "$repo"
git config user.email test@example.com
git config user.name test
commit() {
    for f in "$@"; do mkdir -p "$(dirname "$f")"; echo "$RANDOM$RANDOM" >> "$f"; done
    git add -A && git commit --quiet -m change && git rev-parse HEAD
}

base=$(commit src/Solo.Mobile/capacitor.config.js src/Solo.Desktop/main.js src/Solo.Client/App.razor \
    src/Solo.Shared/Dto.cs src/Solo.Api/Program.cs Icons/Solo.icon/icon.json src/Solo.Mobile/README.md \
    src/Solo.Desktop/README.md src/Solo.Desktop/scripts/stage-icon.mjs)

failures=0
expect() {
    local name=$1 app=$2 want=$3 out
    : > "$GITHUB_STEP_SUMMARY"
    out=$(bash "$gate" "$app" 2>/dev/null)
    local got
    got=$(grep '^upload=' <<< "$out" | cut -d= -f2)
    # stdout is appended to $GITHUB_OUTPUT, where any other line breaks the step.
    if grep -qvE '^(upload|baseline)=' <<< "$out"; then
        echo "FAIL $name: stdout has more than outputs:"
        echo "$out" | sed 's/^/     /'
        failures=$((failures + 1))
    elif [ "$got" = "$want" ]; then
        echo "ok   $name"
    else
        echo "FAIL $name: expected upload=$want, got '${got}'"
        echo "$out" | sed 's/^/     /'
        failures=$((failures + 1))
    fi
}

# Uploaded from $base; nothing since.
echo "100 $base" > "$RUNS"; touch "$UPLOADED/100"
expect "nothing changed since the last upload skips iOS" ios false
expect "nothing changed since the last upload skips Mac" mac false
if grep -q 'TestFlight upload skipped' "$GITHUB_STEP_SUMMARY"; then
    echo "ok   a skip is written to the job summary"
else
    echo "FAIL a skip is not in the job summary"; failures=$((failures + 1))
fi

# A server-only change, which is what most preview tags are.
commit src/Solo.Api/Program.cs > /dev/null
expect "a server change skips iOS" ios false
expect "a server change skips Mac" mac false

# Docs inside the app folders aren't in either app.
commit src/Solo.Mobile/README.md src/Solo.Desktop/README.md > /dev/null
expect "an iOS README change skips iOS" ios false
expect "a desktop README change skips Mac" mac false

# Neither app carries the client: both load the one their instance serves.
commit src/Solo.Client/App.razor > /dev/null
expect "a client change skips iOS" ios false
expect "a client change skips Mac" mac false
commit src/Solo.Shared/Dto.cs > /dev/null
expect "a shared contract change skips Mac" mac false
# The shell itself does reach the Mac app.
commit src/Solo.Desktop/main.js > /dev/null
expect "a desktop shell change uploads Mac" mac true
expect "a desktop shell change skips iOS" ios false

# Icons and the staging script reach both.
reset=$(git rev-parse HEAD)
echo "100 $reset" > "$RUNS"
commit src/Solo.Desktop/scripts/stage-icon.mjs > /dev/null
expect "the icon staging script uploads iOS" ios true
expect "the icon staging script uploads Mac (it's in the shell)" mac true
commit Icons/Solo.icon/icon.json > /dev/null
expect "an icon change uploads iOS" ios true

# The version and preview identity come from release-channel.sh.
reset=$(git rev-parse HEAD)
echo "100 $reset" > "$RUNS"
commit build/release-channel.sh > /dev/null
expect "a release-channel.sh change uploads iOS" ios true
expect "a release-channel.sh change uploads Mac" mac true
echo "100 $(git rev-parse HEAD)" > "$RUNS"
commit Directory.Packages.props > /dev/null
expect "a .NET build setting skips iOS" ios false
expect "a .NET build setting skips Mac" mac false

# The baseline is the last run that uploaded, not the last that succeeded: a
# newer run that skipped (or ran without the key) must not become it.
mobile=$(commit src/Solo.Mobile/capacitor.config.js)
printf '300 %s\n200 %s\n' "$mobile" "$reset" > "$RUNS"
rm -f "$UPLOADED"/*; touch "$UPLOADED/200"
expect "a newer run that didn't upload isn't the baseline" ios true
touch "$UPLOADED/300"
expect "the newest run that uploaded is the baseline" ios false

# The current run, on a re-run, is never its own baseline.
printf '999 %s\n200 %s\n' "$mobile" "$reset" > "$RUNS"
rm -f "$UPLOADED"/*; touch "$UPLOADED/999" "$UPLOADED/200"
expect "the current run is not its own baseline" ios true

# Every uncertain case uploads.
: > "$RUNS"
expect "no earlier upload uploads" ios true
echo "100 0123456789abcdef0123456789abcdef01234567" > "$RUNS"; touch "$UPLOADED/100"
expect "a baseline commit missing from the checkout uploads" ios true
echo "100 $(git rev-parse HEAD)" > "$RUNS"
API_FAILS=1 expect "an unreadable API uploads" ios true
GITHUB_EVENT_NAME=workflow_dispatch expect "a manual run uploads" ios true

if bash "$gate" android > /dev/null 2>&1; then
    echo "FAIL an unknown app was accepted"; failures=$((failures + 1))
else
    echo "ok   an unknown app is refused"
fi

if [ "$failures" -gt 0 ]; then
    echo "$failures check(s) failed." >&2
    exit 1
fi
echo "All TestFlight gate checks passed."
