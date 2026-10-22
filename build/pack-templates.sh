#!/usr/bin/env bash
#
# Packs the `dotnet new` template package for extensions, then proves the
# template generates extensions that build and package:
#
#   HunterAhlquist.Solo.Templates.<version>.nupkg
#
# The proof installs the package into a throwaway template hive, generates an
# extension for each combination of options below, and runs each one's own
# pack.proj. Restore is pinned to the contract packages build/pack-contract.sh
# wrote, as it is for the sample, so a copy on nuget.org can't stand in for the
# contract in this checkout. Then it reads each package back: the manifest has
# to parse, name the id and schema the options asked for, and point at
# assemblies that are in the zip.
#
# Run build/pack-contract.sh first. Nothing is pushed, and nothing is installed
# into your own `dotnet new` templates.
#
# Usage: build/pack-templates.sh [output-directory]

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/templates/Solo.Templates.csproj"
contracts="$root/artifacts/nuget"
output="${1:-$root/artifacts/templates}"

version_in() {
    sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$1" | head -1
}

contract=$(version_in "$root/build/contract-package.props")
version=$(version_in "$project")
if [ -z "$contract" ] || [ -z "$version" ]; then
    echo "error: no <Version> in build/contract-package.props or $project" >&2
    exit 1
fi

# The generated projects pin the contract (build/pack-contract.sh checks that
# pin with the others), so the package's major.minor says which contract it
# scaffolds against. Its patch is its own.
if [ "${version%.*}" != "${contract%.*}" ]; then
    echo "error: the templates are $version but the contract is $contract." >&2
    echo "       Their major.minor must match: bump <Version> in templates/Solo.Templates.csproj." >&2
    exit 1
fi

for id in Extensibility Shared; do
    if [ ! -f "$contracts/HunterAhlquist.Solo.$id.$contract.nupkg" ]; then
        echo "error: no HunterAhlquist.Solo.$id $contract in artifacts/nuget. Run build/pack-contract.sh first." >&2
        exit 1
    fi
done

mkdir -p "$output"
output="$(cd "$output" && pwd)"

echo "Packing the extension templates ${version}…"
dotnet pack "$project" -c Release -o "$output" --nologo
package="$output/HunterAhlquist.Solo.Templates.$version.nupkg"

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT

# A hive of its own: the template is installed and run from here, and your own
# installed templates are neither read nor changed.
hive="$staging/hive"
dotnet new install "$package" --debug:custom-hive "$hive" >/dev/null

# NuGet finds this file by walking up from each generated project.
cat > "$staging/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="contract" value="$contracts" />
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

# A packages folder of its own too, so a contract version already extracted
# from somewhere else is never the one these build against.
export NUGET_PACKAGES="$staging/packages"

(cd "$root" && dotnet tool restore >/dev/null)

# generate <name> <expected id> <expected schema, or -> <client: yes|no> [template options…]
generate() {
    local name="$1" id="$2" schema="$3" client="$4"
    shift 4
    local dir="$staging/$id"

    echo "Generating \"$name\" ($*)…"
    dotnet new solo-extension --debug:custom-hive "$hive" -n "$name" -o "$dir" --no-restore "$@" >/dev/null

    # A conditional the engine didn't recognise is left in the file as text.
    if grep -rnE '^[[:space:]]*(//|<!--|@\*)?#(if|else|elseif|endif)\b' "$dir"; then
        echo "error: \"$name\" still has template conditionals in it." >&2
        exit 1
    fi

    dotnet build "$dir/pack.proj" --nologo -v quiet

    local zip="$dir/artifacts/$id-1.0.0.zip"
    if [ ! -f "$zip" ]; then
        echo "error: \"$name\" did not produce $(basename "$zip")." >&2
        exit 1
    fi

    python3 - "$zip" "$id" "$schema" "$client" <<'PY'
import json, re, sys, zipfile

path, wanted_id, wanted_schema, client = sys.argv[1:5]
package = zipfile.ZipFile(path)
names = set(package.namelist())
manifest = json.loads(package.read("extension.json"))

def fail(message):
    sys.exit(f"error: {path}: {message}")

# What the loader accepts for an id and for a path inside the package.
if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", manifest["id"]):
    fail(f'"{manifest["id"]}" is not a valid id')
for half in ("server", "client"):
    if half in manifest and not re.fullmatch(r"[A-Za-z0-9._/-]+", manifest[half]):
        fail(f'"{manifest[half]}" is not a path the loader accepts')

if manifest["id"] != wanted_id:
    fail(f'id is "{manifest["id"]}", expected "{wanted_id}"')
if manifest.get("schema") != (None if wanted_schema == "-" else wanted_schema):
    fail(f'schema is {manifest.get("schema")!r}, expected {wanted_schema!r}')
if ("client" in manifest) != (client == "yes") or ("nav" in manifest) != (client == "yes"):
    fail("the manifest's client and nav entries don't match the client option")
for half in ("server", "client"):
    if half in manifest and manifest[half] not in names:
        fail(f'the manifest names {manifest[half]}, which is not in the package')

# Only the extension's own assemblies: the instance supplies everything else.
extra = sorted(names - {"extension.json", manifest["server"], manifest.get("client")})
if extra:
    fail(f"unexpected files: {extra}")
PY

    # The template ships a first migration; it has to match the model it ships.
    if [ "$schema" != "-" ]; then
        (cd "$root" && dotnet ef migrations has-pending-model-changes \
            --project "$dir/$name/$name.csproj" --configuration Release --no-build >/dev/null) || {
            echo "error: \"$name\": the template's first migration is out of step with its model." >&2
            echo "       Regenerate templates/content/SoloExtension/*/Migrations." >&2
            exit 1
        }
    fi
}

# The defaults, and a dotted name: the id comes from the whole name, the class
# names from its last segment.
generate "Acme.TimeSheets" acme-time-sheets - yes

# Everything on, under a name an IDE allows and C# and the loader don't: the
# folders keep it, the namespaces and the assemblies get Team_Check_In.
generate "Team Check-In" team-check-in ext_team_check_in yes \
    --nav-scope project --database --hook --events --display-name "Check-ins"

# The server half alone, with an id given rather than derived.
generate "Plain" notes-for-solo - no --client false --extension-id "Notes for Solo"

# The server half with data: no client project to link the contracts into.
generate "Stored" stored ext_stored no --client false --database

echo "Packed to $output:"
ls -1 "$package"
