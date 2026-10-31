#!/usr/bin/env bash
# Publishes the installable HackerOS server for one runtime (ADR 0191 §4) and packs it with its
# install scripts: hackeros-server-<version>-<rid>.zip (Windows) or .tar.gz (Linux, macOS).
#
#   build/publish-server.sh --rid linux-x64 [--version 1.2.3] [--output artifacts/server]
#
# The program is self-contained in one file (.NET does not have to be installed), next to the
# OS it serves (wwwroot) and its settings. Guide: doc/en/wasm/dev/installable-server.md.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID=""
VERSION=""
OUTPUT="$ROOT/artifacts/server"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="${2:?}"; shift 2 ;;
    --version) VERSION="${2:?}"; shift 2 ;;
    --output) OUTPUT="${2:?}"; shift 2 ;;
    *) echo "publish-server.sh: unknown option '$1'" >&2; exit 1 ;;
  esac
done

case "$RID" in
  win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
  *) echo "publish-server.sh: --rid must be win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 or osx-arm64" >&2; exit 1 ;;
esac
if [[ -z "$VERSION" ]]; then
  VERSION="$(sed -n 's:.*<HackerOsVersion>\(.*\)</HackerOsVersion>.*:\1:p' "$ROOT/build/Versions.props")"
fi
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"

NAME="hackeros-server-$VERSION-$RID"
STAGING="$OUTPUT/staging/$NAME"
PACKAGING="$ROOT/Server/HackerOs.Server/packaging"
rm -rf "$STAGING"
mkdir -p "$STAGING"

dotnet publish "$ROOT/Server/HackerOs.Server/HackerOs.Server.csproj" \
  --configuration Release --runtime "$RID" --self-contained \
  --output "$STAGING"
# One file comes from PublishSingleFile in HackerOs.Server.csproj: a -p: here would also reach the
# referenced WebAssembly OS project, which cannot be published that way (NETSDK1098).

# Debug symbols, API documentation and the WebAssembly debugging proxy are not shipped.
find "$STAGING" -maxdepth 1 \( -name '*.pdb' -o -name '*.xml' \) -delete
rm -rf "$STAGING/BlazorDebugProxy"

cp "$PACKAGING/README.txt" "$STAGING/"
if [[ "$RID" == win-* ]]; then
  cp "$PACKAGING/install.ps1" "$PACKAGING/uninstall.ps1" "$STAGING/"
  ARCHIVE="$OUTPUT/$NAME.zip"
  rm -f "$ARCHIVE"
  (cd "$OUTPUT/staging" && zip -q -r "$ARCHIVE" "$NAME")
else
  cp "$PACKAGING/install.sh" "$PACKAGING/uninstall.sh" "$STAGING/"
  chmod +x "$STAGING/install.sh" "$STAGING/uninstall.sh" "$STAGING/HackerOs.Server"
  ARCHIVE="$OUTPUT/$NAME.tar.gz"
  tar -czf "$ARCHIVE" -C "$OUTPUT/staging" "$NAME"
fi
echo "$ARCHIVE"
