#!/usr/bin/env bash
# Removes the Kazo OS Server service and program files (Linux and macOS). ADR 0191.
#   sudo ./uninstall.sh            keeps the data folder (accounts, synchronized files, certificates)
#   sudo ./uninstall.sh --purge    also deletes the data folder
# --prefix and --data must match the ones given to install.sh, if any.
set -euo pipefail

PURGE=0
PREFIX=""
DATA=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --purge) PURGE=1; shift ;;
    --prefix) PREFIX="${2:?}"; shift 2 ;;
    --data) DATA="${2:?}"; shift 2 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) echo "uninstall.sh: unknown option '$1'" >&2; exit 1 ;;
  esac
done

case "$(uname -s)" in
  Linux)
    PREFIX="${PREFIX:-/opt/kazoos-server}"; DATA="${DATA:-/var/lib/kazoos-server}"
    if [[ -f /etc/systemd/system/kazoos-server.service ]]; then
      systemctl disable --now kazoos-server || true
      rm -f /etc/systemd/system/kazoos-server.service
      systemctl daemon-reload
    fi
    ;;
  Darwin)
    PREFIX="${PREFIX:-/usr/local/kazoos-server}"; DATA="${DATA:-/usr/local/var/kazoos-server}"
    PLIST=/Library/LaunchDaemons/ca.kazo.kazoos-server.plist
    if [[ -f "$PLIST" ]]; then
      launchctl bootout system/ca.kazo.kazoos-server 2> /dev/null || true
      rm -f "$PLIST"
    fi
    ;;
  *) echo "uninstall.sh: unsupported system" >&2; exit 1 ;;
esac

# Only a folder that holds the server program is removed, never a mistyped --prefix.
if [[ -f "$PREFIX/Kazo.Os.Server" ]]; then rm -rf "$PREFIX"; fi
if [[ $PURGE -eq 1 ]]; then
  if [[ -d "$DATA" ]]; then rm -rf "$DATA"; fi
  if [[ "$(uname -s)" == "Linux" ]] && id kazoos > /dev/null 2>&1; then userdel kazoos || true; fi
  echo "Kazo OS Server and its data were removed."
else
  echo "Kazo OS Server was removed; its data stays in $DATA (--purge deletes it)."
fi
