#!/usr/bin/env bash
# ADR 0186 §1 and §7: run in the pull request that changes build/Versions.props. Moves every entry
# of changes/unreleased/ into changes/<version>.json (the version of Versions.props, dated today
# unless --date is given), then writes CHANGELOG.md and CHANGELOG-fr.md at the root of the
# repository. Commit the result with the version change.
#
#   build/tools/collect-changes.sh [--date yyyy-MM-dd]
#   build/tools/collect-changes.sh --changelog-only   (only regenerates the two changelogs)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
repository="$(cd "$root/../.." && pwd)"
date="$(date -u +%Y-%m-%d)"
collect=true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --date) date="$2"; shift 2 ;;
    --changelog-only) collect=false; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

version="$(sed -n 's:.*<HackerOsVersion>\(.*\)</HackerOsVersion>.*:\1:p' "$root/build/Versions.props")"
tool=(dotnet run --project "$root/Tools/HackerOs.Tools.ChangeList/HackerOs.Tools.ChangeList.csproj" -c Release --)

if $collect; then
  "${tool[@]}" collect --changes "$root/changes" --version "$version" --date "$date"
fi
"${tool[@]}" changelog --changes "$root/changes" --output-directory "$repository"
echo "Updated CHANGELOG.md and CHANGELOG-fr.md."
