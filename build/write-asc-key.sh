#!/usr/bin/env bash
# Writes the App Store Connect API key in $ASC_KEY to the .p8 file named by $1,
# for the TestFlight jobs. The secret can hold the .p8 file's contents or that
# file in base64, like the other Apple secrets. Anything else fails here with a
# message saying what the secret should hold, rather than later as xcodebuild's
# "invalidPEMDocument". Nothing of the key is ever printed.
set -euo pipefail

dest="$1"
is_pem() { [ "$(head -n 1 "$1")" = '-----BEGIN PRIVATE KEY-----' ] && [ "$(tail -n 1 "$1")" = '-----END PRIVATE KEY-----' ]; }

umask 077
mkdir -p "$(dirname "$dest")"
# Line endings from a Windows paste break the PEM parser.
printf '%s\n' "$ASC_KEY" | tr -d '\r' | sed '/^[[:space:]]*$/d' > "$dest"
if is_pem "$dest"; then
    exit 0
fi

if printf '%s' "$ASC_KEY" | tr -d '[:space:]' | base64 --decode 2>/dev/null | tr -d '\r' | sed '/^[[:space:]]*$/d' > "$dest.decoded" \
    && is_pem "$dest.decoded"; then
    mv "$dest.decoded" "$dest"
    exit 0
fi

rm -f "$dest" "$dest.decoded"
echo "::error title=App Store Connect key::APP_STORE_CONNECT_KEY isn't a .p8 key. Paste the whole AuthKey_<id>.p8 file, from -----BEGIN PRIVATE KEY----- through -----END PRIVATE KEY----- on their own lines, or that file in base64."
exit 1
