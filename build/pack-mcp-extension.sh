#!/usr/bin/env bash
#
# Packages src/Solo.Mcp as an installable Solo extension:
#
#   solo-mcp-<version>.zip
#   ├── extension.json
#   └── server/Solo.Mcp.dll
#
# The manifest's version is the package's identity — the host records it, the
# downgrade guard compares against it, and it names this file — so the script
# refuses to run when the manifest and the project disagree about it.
#
# Only assemblies the host does NOT already provide are packaged. Shipping a
# private copy of Solo.Shared or Solo.Extensibility is the classic plugin bug:
# the loader resolves those from the default context, so a copy in the package is
# either ignored or, worse, loaded as a second distinct set of types. The filter
# below mirrors ExtensionLoadContext.IsShared.
#
# Usage: build/pack-mcp-extension.sh [output-directory]

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/Solo.Mcp"
output="${1:-$root/artifacts}"

manifest_version=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$project/extension.json" | head -1)
project_version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$project/Solo.Mcp.csproj" | head -1)

if [ -z "$manifest_version" ]; then
    echo "error: no \"version\" in $project/extension.json" >&2
    exit 1
fi

if [ "$manifest_version" != "$project_version" ]; then
    echo "error: extension.json says $manifest_version but Solo.Mcp.csproj says $project_version." >&2
    echo "       Bump both — the manifest names the package, the project stamps the assembly." >&2
    exit 1
fi

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

echo "Publishing Solo.Mcp $manifest_version…"
dotnet publish "$project/Solo.Mcp.csproj" -c Release -o "$staging/publish" --nologo

package="$staging/package"
mkdir -p "$package/server"
cp "$project/extension.json" "$package/extension.json"

# Everything the host does not already have loaded.
for dll in "$staging/publish"/*.dll; do
    name=$(basename "$dll" .dll)
    case "$name" in
        Solo.Shared|Solo.Extensibility|netstandard|mscorlib) continue ;;
        System.*|Microsoft.*|Npgsql*) continue ;;
    esac
    cp "$dll" "$package/server/"
done

if [ ! -f "$package/server/Solo.Mcp.dll" ]; then
    echo "error: the publish output contained no Solo.Mcp.dll." >&2
    exit 1
fi

mkdir -p "$output"
archive="$output/solo-mcp-$manifest_version.zip"
rm -f "$archive"
# -X drops extra file attributes so the same input produces the same bytes.
(cd "$package" && zip -qrX "$archive" extension.json server)

echo "Packaged $archive"
unzip -l "$archive"
