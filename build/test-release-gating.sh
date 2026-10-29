#!/usr/bin/env bash
# A lint on the desktop release job's own structure.
#
# Three review rounds in a row found a release published after one of its
# producing steps had failed, and every time the cause was the same: a
# hand-written `if:` on the publishing steps had to name every producing step
# before it, and missed one. `!cancelled()` made it worse by dropping the
# implicit success check altogether, so a guard step could `exit 1` and the
# upload would still run.
#
# So the rule is structural rather than enumerated: from the baseline step
# through the release upload, no step carries an `if:` at all. GitHub's default
# gating then applies — a step runs only if every earlier step succeeded — and
# there is no condition to keep in step when a step is added between them.
#
# The one tolerated failure is building the updater feed, whose
# `continue-on-error` lets the steps that keep the release usable run before the
# job gives up. That failure is re-asserted by the last step, so it cannot be
# lost.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
workflow="$root/.github/workflows/release-desktop.yml"

begin='      - name: Resolve the baseline manifest'
end='      - name: Drop the hand-off artifacts'

for marker in "$begin" "$end"; do
    if ! grep -qxF "$marker" "$workflow"; then
        echo "Expected a step named '${marker##*: }' in release-desktop.yml; the lint below needs it as a landmark." >&2
        exit 1
    fi
done

region=$(awk -v b="$begin" -v e="$end" '$0 == b {inside = 1} $0 == e {inside = 0} inside' "$workflow")

# The upload has to be inside the region, or the lint is checking nothing.
if ! grep -q 'action-gh-release' <<< "$region"; then
    echo "The release upload is no longer between the baseline step and the artifact sweep;" >&2
    echo "this lint would pass without checking it." >&2
    exit 1
fi

if conditions=$(grep -n '^        if:' <<< "$region"); then
    echo "A step between resolving the baseline and publishing the release carries an 'if:':" >&2
    echo "$conditions" >&2
    echo >&2
    echo "Remove it. These steps must use default gating so that any earlier failure stops the" >&2
    echo "publish; a hand-written condition has to name every producing step before it, and that" >&2
    echo "is what published a release with no manifest three times over." >&2
    exit 1
fi

# The single tolerated failure, and the step that re-asserts it.
tolerated=$(grep -c '^        continue-on-error: true$' "$workflow" || true)
if [ "$tolerated" != "1" ]; then
    echo "Expected exactly one 'continue-on-error: true' (the updater feed write), found ${tolerated}." >&2
    echo "Every other failure must stop the publish." >&2
    exit 1
fi

if ! grep -q "steps.written.outcome == 'failure'" "$workflow"; then
    echo "Nothing re-asserts a failed updater feed write, so 'continue-on-error' would swallow it" >&2
    echo "and the job would report success." >&2
    exit 1
fi

# That re-assertion has to come after the upload, or it would stop the very
# steps that make the release consistent before the job gives up.
upload_line=$(grep -n 'action-gh-release' "$workflow" | tail -1 | cut -d: -f1)
report_line=$(grep -n "steps.written.outcome == 'failure'" "$workflow" | tail -1 | cut -d: -f1)
if [ "$report_line" -lt "$upload_line" ]; then
    echo "The step reporting a failed updater feed comes before the release upload, so it would" >&2
    echo "prevent the fallback from ever being published." >&2
    exit 1
fi

echo "Release gating checks passed."
