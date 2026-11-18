#!/usr/bin/env bash
# Writes the App Store Connect API key in $ASC_KEY to the .p8 file named by $1,
# for the TestFlight jobs. The secret can hold the .p8 file's contents, that
# file in base64 (like the other Apple secrets), or just the key's base64 body
# without the BEGIN/END lines. Anything else fails here with a message saying
# what the secret should hold, rather than later as xcodebuild's
# "invalidPEMDocument". Nothing of the key is ever printed.
set -euo pipefail
# The secret may decode to binary; tr and sed refuse that under a UTF-8 locale.
export LC_ALL=C

dest="$1"
begin='-----BEGIN PRIVATE KEY-----'
end='-----END PRIVATE KEY-----'
clean() { tr -d '\r' | sed '/^[[:space:]]*$/d'; }
is_pem() { [ "$(head -n 1 "$1" | tr -d '\0')" = "$begin" ] && [ "$(tail -n 1 "$1" | tr -d '\0')" = "$end" ]; }

umask 077
mkdir -p "$(dirname "$dest")"

printf '%s\n' "$ASC_KEY" | clean > "$dest"
if is_pem "$dest"; then
    exit 0
fi

body=$(printf '%s' "$ASC_KEY" | tr -d '[:space:]')
if printf '%s' "$body" | base64 --decode 2>/dev/null | clean > "$dest" && is_pem "$dest"; then
    exit 0
fi

# A key body is base64 of DER, which starts with an ASN.1 SEQUENCE (0x30).
if [ "$(printf '%s' "$body" | base64 --decode 2>/dev/null | head -c 1 | od -An -tx1 | tr -d ' ')" = 30 ]; then
    { echo "$begin"; printf '%s' "$body" | fold -w 64; echo; echo "$end"; } | clean > "$dest"
    exit 0
fi

rm -f "$dest"
echo "::error title=App Store Connect key::APP_STORE_CONNECT_KEY isn't a .p8 key. Paste the whole AuthKey_<id>.p8 file, from $begin through $end on their own lines, or that file in base64."
exit 1
