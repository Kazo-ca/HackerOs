#!/usr/bin/env bash
# ADR 0186 section 5: fails when the commits between <base> and <head> change the product but add no
# entry to the change list. The product is wasm2/HackerOs outside Tests/, docs/, changes/ and
# Markdown files. An entry is a new file wasm2/HackerOs/changes/unreleased/<guid>.json, or the new
# changes/<version>.json of a release pull request (formats checked by ChangeEntriesTests). The
# Change list entry workflow skips this script when the pull request carries the no-changelog label.
#
#   build/tools/check-change-entry.sh <base> <head>   (default: origin/main HEAD)
set -euo pipefail

base="${1:-origin/main}"
head="${2:-HEAD}"
merge_base="$(git merge-base "$base" "$head")"

product=()
while IFS= read -r path; do
  case "$path" in
    wasm2/HackerOs/Tests/*|wasm2/HackerOs/docs/*|wasm2/HackerOs/changes/*|*.md) ;;
    wasm2/HackerOs/*) product+=("$path") ;;
  esac
done < <(git diff --name-only "$merge_base" "$head")

entries=()
while IFS= read -r path; do
  # A release pull request (collect-changes.sh) adds changes/<version>.json instead.
  [[ "$path" == wasm2/HackerOs/changes/unreleased/*.json || "$path" =~ ^wasm2/HackerOs/changes/[0-9]+\.[0-9]+\.[0-9]+\.json$ ]] && entries+=("$path")
done < <(git diff --name-only --diff-filter=A "$merge_base" "$head")

summary="${GITHUB_STEP_SUMMARY:-/dev/null}"
if [[ ${#product[@]} -eq 0 ]]; then
  echo "No product file changed: no change list entry is required." | tee -a "$summary"
  exit 0
fi

if [[ ${#entries[@]} -gt 0 ]]; then
  echo "Change list entries added: ${entries[*]}" | tee -a "$summary"
  exit 0
fi

{
  echo "This pull request changes ${#product[@]} product file(s), for example ${product[0]},"
  echo "but adds no entry in wasm2/HackerOs/changes/unreleased/<guid>.json."
  echo "Add one (doc/en/wasm/dev/change-list.md), or set the no-changelog label when the change is"
  echo "invisible to users and app developers (refactoring, tests, build only)."
} | tee -a "$summary" >&2
exit 1
