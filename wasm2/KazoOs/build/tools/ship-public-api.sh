#!/usr/bin/env bash
# ADR 0114 §3, ADR 0132 §2: folds every PublicAPI.Unshipped.txt of the solution into its
# PublicAPI.Shipped.txt when a version of the SDK is published (1.1.0 by hand in v3.1/4, 1.2.0 with
# this script in exec/10). For each library:
#   - a "*REMOVED*<line>" entry of Unshipped deletes <line> from Shipped;
#   - every other entry is added to Shipped;
#   - Shipped is written back "#nullable enable" first, then the lines in ordinal order (LC_ALL=C),
#     the order every Shipped file already has;
#   - Unshipped is reset to "#nullable enable".
# Bump build/Versions.props separately: the content of Unshipped says which version is needed
# (patch, minor for an addition, major once a *REMOVED* touches the SDK itself).
#
#   build/tools/ship-public-api.sh [--check]   (--check: list what would be shipped, change nothing)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
check=false
[[ "${1:-}" == "--check" ]] && check=true

mapfile -t unshipped < <(cd "$root" && find . -name PublicAPI.Unshipped.txt -not -path '*/obj/*' -not -path '*/bin/*' | sort)
total=0
for file in "${unshipped[@]}"; do
  dir="$root/${file%/PublicAPI.Unshipped.txt}"
  shipped="$dir/PublicAPI.Shipped.txt"
  mapfile -t entries < <(grep -v '^#nullable enable$' "$root/$file" | sed '/^[[:space:]]*$/d' || true)
  [[ ${#entries[@]} -eq 0 ]] && continue
  total=$((total + ${#entries[@]}))
  echo "${file#./}: ${#entries[@]} entries"
  $check && continue

  [[ -f "$shipped" ]] || { echo "No PublicAPI.Shipped.txt next to $file" >&2; exit 1; }
  work="$(mktemp)"
  grep -v '^#nullable enable$' "$shipped" | sed '/^[[:space:]]*$/d' > "$work" || true
  for entry in "${entries[@]}"; do
    if [[ "$entry" == '*REMOVED*'* ]]; then
      removed="${entry#\*REMOVED\*}"
      grep -vxF -- "$removed" "$work" > "$work.next" || true
      if cmp -s "$work" "$work.next"; then
        echo "  $file: removed line not found in Shipped: $removed" >&2
        rm -f "$work" "$work.next"
        exit 1
      fi
      mv "$work.next" "$work"
    else
      printf '%s\n' "$entry" >> "$work"
    fi
  done
  { echo '#nullable enable'; LC_ALL=C sort -u "$work"; } > "$shipped"
  printf '#nullable enable\n' > "$root/$file"
  rm -f "$work"
done
echo "$total entries $($check && echo 'to ship' || echo 'shipped')."
