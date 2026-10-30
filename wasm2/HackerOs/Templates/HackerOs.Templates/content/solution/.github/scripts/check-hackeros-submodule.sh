#!/usr/bin/env bash
# ADR 0176 §6: the HackerOS submodule is never modified in an app repository, and it sits exactly
# on the release tag of the version it declares (build/Versions.props). A local change of the OS
# can therefore never reach a published build.
set -euo pipefail

submodule="external/HackerOs"
versions="$submodule/wasm2/HackerOs/build/Versions.props"
if [[ ! -f "$versions" ]]; then
  echo "::error::$versions not found. Run: git submodule update --init"
  exit 1
fi

version="$(sed -n 's:.*<HackerOsVersion>\(.*\)</HackerOsVersion>.*:\1:p' "$versions")"
if [[ -n "$(git -C "$submodule" status --porcelain)" ]]; then
  echo "::error::$submodule has local changes. The OS is not modified from an app repository."
  git -C "$submodule" status --short
  exit 1
fi

git -C "$submodule" fetch --quiet --depth 1 origin "refs/tags/v$version:refs/tags/v$version"
head="$(git -C "$submodule" rev-parse HEAD)"
tag="$(git -C "$submodule" rev-parse "v$version^{commit}")"
if [[ "$head" != "$tag" ]]; then
  echo "::error::$submodule is at $head, not on the tag v$version ($tag). Move it with: git -C $submodule checkout v$version"
  exit 1
fi
echo "HackerOS submodule: v$version ($head), clean."
