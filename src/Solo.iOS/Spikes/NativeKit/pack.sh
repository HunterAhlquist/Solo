#!/bin/bash
# pack.sh <directory> : builds the spike extension and writes kit-spike-0.1.0.zip into the directory,
# which is an instance's extensions directory or somewhere to copy it from. Needs build/pack-contract.sh
# to have been run, for the contract packages.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="${1:?usage: pack.sh <directory>}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dotnet build "$here/KitSpike.Client/KitSpike.Client.csproj" -c Release -o "$work/build" --nologo -v q
mkdir -p "$work/pkg/client" "$work/pkg/content"
cp "$work/build/KitSpike.Client.dll" "$work/pkg/client/"
cp "$here/content/"* "$work/pkg/content/"
cp "$here/extension.json" "$work/pkg/"
rm -f "$out/kit-spike-0.1.0.zip"
(cd "$work/pkg" && zip -qr "$out/kit-spike-0.1.0.zip" .)
echo "$out/kit-spike-0.1.0.zip"
