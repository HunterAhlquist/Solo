#!/usr/bin/env bash
#
# Packages the MCP extension — both halves — as an installable Solo package:
#
#   solo-mcp-<version>.zip
#   ├── extension.json
#   ├── server/Solo.Mcp.dll         loaded in-process by the API
#   ├── client/Solo.Mcp.Client.dll  downloaded by the browser, handed to the router
#   └── content/mcp.css             served at /api/extensions/mcp/content/
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
# Usage: build/pack-mcp-extension.sh [output-directory]

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
server="$root/src/Solo.Mcp"
client="$root/src/Solo.Mcp.Client"
output="${1:-$root/artifacts}"

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

for project in "$server/Solo.Mcp.csproj" "$client/Solo.Mcp.Client.csproj"; do
    declared=$(version_of_project "$project")
    if [ "$declared" != "$version" ]; then
        echo "error: extension.json says $version but $(basename "$project") says $declared." >&2
        echo "       Bump all three — the manifest names the package, the projects stamp the assemblies." >&2
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

echo "Publishing Solo.Mcp $version (server)…"
dotnet publish "$server/Solo.Mcp.csproj" -c Release -o "$staging/server" --nologo
stage_private_assemblies "$staging/server" "$package/server" "Solo.Mcp.dll"

echo "Publishing Solo.Mcp.Client $version (client)…"
dotnet build "$client/Solo.Mcp.Client.csproj" -c Release -o "$staging/client" --nologo
stage_private_assemblies "$staging/client" "$package/client" "Solo.Mcp.Client.dll"

cp "$server/extension.json" "$package/extension.json"
if [ -d "$server/content" ]; then
    cp -R "$server/content" "$package/content"
fi

mkdir -p "$output"
archive="$output/solo-mcp-$version.zip"
rm -f "$archive"
# -X drops extra file attributes so the same input produces the same bytes.
(cd "$package" && zip -qrX "$archive" .)

echo "Packaged $archive"
unzip -l "$archive"
