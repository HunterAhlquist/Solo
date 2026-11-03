#!/usr/bin/env bash
# Exercises build/nuget-behind.sh against canned flat-container indexes.
set -euo pipefail

cd "$(dirname "$0")/.."

expect() {
    local index=$1 version=$2 want=$3
    local got
    got=$(NUGET_INDEX_JSON="$index" bash build/nuget-behind.sh HunterAhlquist.Solo.Shared "$version" 2>/dev/null)
    if [ "$got" != "$want" ]; then
        echo "FAIL: ${version} against ${index}: got '${got}', want '${want}'" >&2
        exit 1
    fi
}

listed='{"versions":["1.8.0","1.9.0","1.11.0","1.11.1"]}'

expect "$listed" 1.15.0 behind=true    # the train carries a newer contract
expect "$listed" 1.11.1 behind=false   # already published
expect "$listed" 1.10.0 behind=false   # a lowered version is never pushed
expect '{"versions":[]}' 1.0.0 behind=true   # a package nuget.org has never seen
expect '{"versions":["1.2.0","1.10.0"]}' 1.9.0 behind=false   # numeric, not lexical, order

echo "nuget-behind: ok"
