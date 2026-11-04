#!/usr/bin/env bash
# ADR 0098 §8, ADR 0114, ADR 0132 §1, ADR 0161 §3: builds one SDK compatibility fixture (a project
# of src/) against the Kazo OS packages of a local feed, the way another repository would, and
# writes its package to packages/. Run once per published SDK version that gets a fixture (1.0.0:
# Contoso.Sdk1Compat; 1.1.0: Contoso.Sdk11Compat, packed from the v3.1/base tip; 2.0.0:
# Contoso.Sdk2Compat, packed from the desk/6-sdk-2 tip); the committed package is never rebuilt, so
# the tests keep loading an app compiled against the old SDK into the current OS (or, for an
# earlier major, check that the OS refuses it).
#
#   Tests/fixtures/sdk-compat/produce-fixture.sh [--project <name>] [--version <x.y.z>] [--feed <dir>]
#     --project  a folder of src/ (default: Contoso.Sdk1Compat)
#     --version  the SDK version of the feed's packages (default: HackerOsVersion of build/Versions.props)
#     --feed     the folder of Kazo OS packages (default: artifacts/local-nupkg)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
feed="$root/artifacts/local-nupkg"
project="Contoso.Sdk1Compat"
version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --feed) feed="$(cd "$2" && pwd)"; shift 2 ;;
    --project) project="$2"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

[[ -d "$here/src/$project" ]] || { echo "No project src/$project." >&2; exit 2; }
[[ -n "$version" ]] || version="$(sed -n 's:.*<HackerOsVersion>\(.*\)</HackerOsVersion>.*:\1:p' "$root/build/Versions.props")"
[[ -f "$feed/Kazo.Os.AppSdk.$version.nupkg" ]] || { echo "No Kazo.Os.AppSdk.$version.nupkg in $feed: run build/pack-local.sh first." >&2; exit 1; }

# Built outside the repository: no Directory.Build.* of Kazo OS, an isolated NuGet cache.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp -r "$here/src/." "$work/"
cat > "$work/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config><add key="globalPackagesFolder" value="$work/packages" /></config>
  <packageSources>
    <clear />
    <add key="hackeros-local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="hackeros-local"><package pattern="Kazo.Os.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
XML

mkdir -p "$here/packages"
MSBUILDDISABLENODEREUSE=1 dotnet pack "$work/$project/$project.csproj" -c Release \
  -p:HackerOsVersion="$version" -p:ContinuousIntegrationBuild=true -o "$here/packages"
echo "Fixture: $here/packages/$project.$version.nupkg"
