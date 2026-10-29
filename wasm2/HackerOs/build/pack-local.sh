#!/usr/bin/env bash
# ADR 0098 §2: packs every library HackerOS publishes (each project importing
# Platform/Packaging.props, plus the templates) into artifacts/local-nupkg.
# Another repository consumes the folder through a nuget.config (sample:
# build/nuget.config.sample). The Release workflow (ADR 0173) runs the same script with
# --ci, then pushes the packages to nuget.org.
#
#   build/pack-local.sh [--configuration Release] [--output <dir>] [--ci]
#
# --ci: ContinuousIntegrationBuild=true (deterministic source paths, for published packages).
#
# Re-packing the same version would leave NuGet's global cache serving the previous
# bytes, so the script also removes that version of each HackerOs.* package from it.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Release"
output="$root/artifacts/local-nupkg"
continuous_integration=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration) configuration="$2"; shift 2 ;;
    -o|--output) output="$2"; shift 2 ;;
    --ci) continuous_integration=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

version="$(sed -n 's:.*<HackerOsVersion>\(.*\)</HackerOsVersion>.*:\1:p' "$root/build/Versions.props")"
mkdir -p "$output" "$root/artifacts"

# Solution filter of the published projects (paths relative to the solution).
filter="$root/artifacts/packages.slnf"
mapfile -t projects < <(cd "$root" && grep -rl --include='*.csproj' -e 'Packaging.props' . \
  | grep -v '/obj/' | grep -v '/bin/' | sed 's:^\./::' | sort)
{
  echo '{ "solution": { "path": "../HackerOs.sln", "projects": ['
  for i in "${!projects[@]}"; do
    sep=","; [[ $i -eq $((${#projects[@]} - 1)) ]] && sep=""
    printf '  "%s"%s\n' "${projects[$i]//\//\\\\}" "$sep"
  done
  echo '] } }'
} > "$filter"

# ADR 0186 §7: PackageReleaseNotes of every package = the developer entries of this version
# (changes/<version>.json, or changes/unreleased/ before the version is collected) and the Release link.
notes="$root/artifacts/package-release-notes.txt"
dotnet run --project "$root/Tools/HackerOs.Tools.ChangeList/HackerOs.Tools.ChangeList.csproj" -c Release -- \
  notes --changes "$root/changes" --version "$version" --kind package --output "$notes"

echo "Packing ${#projects[@]} projects, version $version, into $output"
dotnet pack "$filter" -c "$configuration" -o "$output" -p:ContinuousIntegrationBuild="$continuous_integration" \
  -p:HackerOsPackageReleaseNotesFile="$notes"

cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
for package in "$output"/*."$version".nupkg; do
  id="$(basename "$package" ".$version.nupkg" | tr '[:upper:]' '[:lower:]')"
  rm -rf "${cache:?}/$id/$version"
done
echo "Done: $(ls "$output"/*."$version".nupkg | wc -l) packages."
