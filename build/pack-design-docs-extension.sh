#!/usr/bin/env bash
#
# Packages the Design Docs extension — both halves and the browser module — as
# an installable Solo package:
#
#   solo-design-docs-<version>[-<label>].zip
#   ├── extension.json
#   ├── server/Solo.DesignDocs.dll         loaded in-process by the API
#   ├── client/Solo.DesignDocs.Client.dll  downloaded by the browser, handed to the router
#   └── content/design-docs.js, .css,      served at /api/extensions/design-docs/content/
#       silkscreen.woff2 (+ its licence)
#
# The browser module is built here from Solo.DesignDocs.Client/js (npm ci, its
# tests, then esbuild), so the package never carries a stale bundle. Needs Node.
#
# A label (a preview build's agent name, say) only renames the file. The
# manifest id stays design-docs: two labelled zips are the same extension, so
# an instance installs one of them at a time.
#
# The manifest's version is the package's identity — the host records it, the
# downgrade guard compares against it, and it names this file — so the script
# refuses to run when the manifest and either project disagree about it.
#
# Only assemblies the host does NOT already provide are packaged. Shipping a
# private copy of Solo.Shared, MudBlazor or the framework is the classic plugin
# bug: those resolve from the host, so a copy in the package is either ignored
# or, worse, loaded as a second distinct set of types. The filter below mirrors
# ExtensionLoadContext.IsShared on the server, and what the browser has already
# loaded on the client.
#
# Both halves build from the contract packages, through extensions/nuget.config,
# so build/pack-contract.sh has to have packed them first.
#
# Usage: build/pack-design-docs-extension.sh [output-directory] [label]

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
server="$root/extensions/DesignDocs/Solo.DesignDocs"
client="$root/extensions/DesignDocs/Solo.DesignDocs.Client"
output="${1:-$root/artifacts}"
label="${2:-}"
if [ -n "$label" ] && ! [[ "$label" =~ ^[a-z][a-z0-9-]*$ ]]; then
    echo "error: a label is lowercase letters, digits and hyphens: $label" >&2
    exit 1
fi

version_of_manifest() {
    sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$1" | head -1
}

version_of_project() {
    sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$1" | head -1
}

version=$(version_of_manifest "$server/extension.json")
if [ -z "$version" ]; then
    echo "error: no \"version\" in $server/extension.json" >&2
    exit 1
fi

for project in "$server/Solo.DesignDocs.csproj" "$client/Solo.DesignDocs.Client.csproj"; do
    declared=$(version_of_project "$project")
    if [ "$declared" != "$version" ]; then
        echo "error: extension.json says $version but $(basename "$project") says $declared." >&2
        echo "       Bump all three — the manifest names the package, the projects stamp the assemblies." >&2
        exit 1
    fi
done

# Restore would fail on its own without the packages, but naming the step is
# kinder than NU1101. artifacts/nuget is the source extensions/nuget.config
# maps them to.
contract=$(sed -n 's/.*Include="HunterAhlquist\.Solo\.Extensibility" Version="\([^"]*\)".*/\1/p' \
    "$server/Solo.DesignDocs.csproj" | head -1)
for id in Extensibility Shared; do
    if [ ! -f "$root/artifacts/nuget/HunterAhlquist.Solo.$id.$contract.nupkg" ]; then
        echo "error: no HunterAhlquist.Solo.$id $contract in artifacts/nuget. Run build/pack-contract.sh first." >&2
        exit 1
    fi
done

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT
package="$staging/package"
mkdir -p "$package/server" "$package/client"

# Copies every published assembly the host does not already have loaded.
stage_private_assemblies() {
    local from="$1" to="$2" main="$3"
    for dll in "$from"/*.dll; do
        name=$(basename "$dll" .dll)
        case "$name" in
            Solo.Shared|Solo.Extensibility|MudBlazor*|netstandard|mscorlib) continue ;;
            System.*|Microsoft.*|Npgsql*) continue ;;
        esac
        cp "$dll" "$to/"
    done

    if [ ! -f "$to/$main" ]; then
        echo "error: the publish output contained no $main." >&2
        exit 1
    fi
}

echo "Building the browser module…"
(cd "$client/js" && npm ci --no-audit --no-fund && npm test && npm run build)

echo "Publishing Solo.DesignDocs $version (server)…"
dotnet publish "$server/Solo.DesignDocs.csproj" -c Release -o "$staging/server" --nologo
stage_private_assemblies "$staging/server" "$package/server" "Solo.DesignDocs.dll"

echo "Publishing Solo.DesignDocs.Client $version (client)…"
dotnet build "$client/Solo.DesignDocs.Client.csproj" -c Release -o "$staging/client" --nologo
stage_private_assemblies "$staging/client" "$package/client" "Solo.DesignDocs.Client.dll"

cp "$server/extension.json" "$package/extension.json"
mkdir -p "$package/content"
for file in design-docs.js design-docs.css silkscreen.woff2 silkscreen-LICENSE.txt; do
    cp "$server/content/$file" "$package/content/$file"
done

mkdir -p "$output"
archive="$output/solo-design-docs-$version${label:+-$label}.zip"
rm -f "$archive"
# -X drops extra file attributes so the same input produces the same bytes.
(cd "$package" && zip -qrX "$archive" .)

echo "Packaged $archive"
unzip -l "$archive"
