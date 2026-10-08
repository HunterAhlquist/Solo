#!/usr/bin/env bash
#
# Packs the extension contract — the two assemblies an extension compiles
# against — as NuGet packages, then proves an extension builds from them alone:
#
#   HunterAhlquist.Solo.Shared.<version>.nupkg          an extension's client half
#   HunterAhlquist.Solo.Extensibility.<version>.nupkg   its server half
#
# The proof copies samples/HelloExtension out of the repository and builds it
# with a throwaway packages folder and source mapping that pins
# HunterAhlquist.Solo.* to the packages just made, so neither a ProjectReference
# nor a copy already on nuget.org can stand in for them. If the sample builds,
# an author outside this repository can build an extension.
#
# Nothing is pushed. Publishing to nuget.org is a separate, deliberate step.
#
# Usage: build/pack-contract.sh [output-directory]

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props="$root/build/contract-package.props"
sample="$root/samples/HelloExtension"
output="${1:-$root/artifacts/nuget}"

version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$props" | head -1)
if [ -z "$version" ]; then
    echo "error: no <Version> in $props" >&2
    exit 1
fi

# The sample pins the contract version literally, as an author's project would,
# so it has to be bumped with the contract or it would quietly prove an older one.
for project in "$sample"/*/*.csproj; do
    pinned=$(sed -n 's/.*Include="HunterAhlquist\.Solo\.[A-Za-z]*" Version="\([^"]*\)".*/\1/p' "$project" | head -1)
    if [ "$pinned" != "$version" ]; then
        echo "error: $(basename "$project") references contract ${pinned:-(none)} but the contract is $version." >&2
        echo "       Bump the sample's HunterAhlquist.Solo.* references with build/contract-package.props." >&2
        exit 1
    fi
done

mkdir -p "$output"
output="$(cd "$output" && pwd)"

echo "Packing the extension contract $version…"
dotnet pack "$root/src/Solo.Shared/Solo.Shared.csproj" -c Release -o "$output" --nologo
dotnet pack "$root/src/Solo.Extensibility/Solo.Extensibility.csproj" -c Release -o "$output" --nologo

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

cp -R "$sample" "$staging/sample"
find "$staging/sample" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

cat > "$staging/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="contract" value="$output" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="contract">
      <package pattern="HunterAhlquist.Solo.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

echo "Building samples/HelloExtension from the packages alone…"
dotnet restore "$staging/sample/Hello.slnx" --configfile "$staging/nuget.config" \
    --packages "$staging/packages" --nologo
dotnet build "$staging/sample/Hello.slnx" -c Release --no-restore --nologo

echo "Packed to $output:"
ls -1 "$output"/HunterAhlquist.Solo.*."$version".*nupkg
