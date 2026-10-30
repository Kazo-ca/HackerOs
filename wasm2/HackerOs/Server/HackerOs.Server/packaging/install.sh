#!/usr/bin/env bash
# HackerOS Server installer for Linux (systemd) and macOS (launchd). ADR 0191.
# Guide: https://github.com/Kazo-ca/HackerOs/blob/main/doc/en/wasm/dev/installable-server.md
#
#   sudo ./install.sh                         this computer only: http://localhost:5888
#   sudo ./install.sh --lan 192.168.1.20      also the local network in HTTPS (https://192.168.1.20:5889)
#
# Options:
#   --lan <names>       comma-separated IP addresses or host names other devices use to reach this
#                       machine; generates a local certificate authority and an HTTPS certificate
#   --port <n>          local HTTP port (default 5888)
#   --https-port <n>    local network HTTPS port (default 5889)
#   --account <name>    server account the first pairing code is for (default admin)
#   --prefix <dir>      install folder (default /opt/hackeros-server, macOS /usr/local/hackeros-server)
#   --data <dir>        data folder (default /var/lib/hackeros-server, macOS /usr/local/var/hackeros-server)
#   --no-service        install and configure only; do not register or start a service
#   --reconfigure       rewrite hackeros-server.json even if it exists (an upgrade keeps it otherwise)
set -euo pipefail

PORT=5888
HTTPS_PORT=5889
LAN=""
ACCOUNT="admin"
PREFIX=""
DATA=""
SERVICE=1
RECONFIGURE=0
PAGES_ORIGIN="https://kazo-ca.github.io"
SERVICE_NAME="hackeros-server"
LAUNCHD_LABEL="ca.kazo.hackeros-server"

fail() { echo "install.sh: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --lan) LAN="${2:?--lan needs a value}"; shift 2 ;;
    --port) PORT="${2:?}"; shift 2 ;;
    --https-port) HTTPS_PORT="${2:?}"; shift 2 ;;
    --account) ACCOUNT="${2:?}"; shift 2 ;;
    --prefix) PREFIX="${2:?}"; shift 2 ;;
    --data) DATA="${2:?}"; shift 2 ;;
    --no-service) SERVICE=0; shift ;;
    --reconfigure) RECONFIGURE=1; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) fail "unknown option '$1' (see --help)" ;;
  esac
done

[[ "$PORT" =~ ^[0-9]+$ && "$HTTPS_PORT" =~ ^[0-9]+$ ]] || fail "ports must be numbers"
[[ "$ACCOUNT" =~ ^[A-Za-z0-9._-]{1,64}$ ]] || fail "account name: letters, digits, '.', '_' or '-' only"

SYSTEM="$(uname -s)"
case "$SYSTEM" in
  Linux) PREFIX="${PREFIX:-/opt/hackeros-server}"; DATA="${DATA:-/var/lib/hackeros-server}" ;;
  Darwin) PREFIX="${PREFIX:-/usr/local/hackeros-server}"; DATA="${DATA:-/usr/local/var/hackeros-server}" ;;
  *) fail "unsupported system '$SYSTEM' (Linux or macOS; Windows uses install.ps1)" ;;
esac

if [[ $SERVICE -eq 1 && $EUID -ne 0 ]]; then
  fail "run as root (sudo ./install.sh), or with --no-service --prefix <dir> --data <dir> for a user install"
fi

SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[[ -f "$SOURCE/HackerOs.Server" ]] || fail "HackerOs.Server not found next to install.sh"

# The account the service runs as: a system user on Linux, the user who ran sudo on macOS.
RUN_USER="$(id -un)"
if [[ $SERVICE -eq 1 ]]; then
  if [[ "$SYSTEM" == "Linux" ]]; then
    RUN_USER="hackeros"
    if ! id "$RUN_USER" > /dev/null 2>&1; then
      useradd --system --no-create-home --home-dir "$DATA" --shell /usr/sbin/nologin "$RUN_USER"
    fi
  else
    RUN_USER="${SUDO_USER:-$(id -un)}"
  fi
fi

stop_service() {
  if [[ $SERVICE -eq 0 ]]; then return; fi
  if [[ "$SYSTEM" == "Linux" ]]; then
    systemctl stop "$SERVICE_NAME" 2> /dev/null || true
  else
    launchctl bootout "system/$LAUNCHD_LABEL" 2> /dev/null || true
  fi
}

# ── Files ─────────────────────────────────────────────────────────────────────
echo "Installing HackerOS Server into $PREFIX (data: $DATA)"
stop_service
mkdir -p "$PREFIX" "$DATA/blobs" "$DATA/backups" "$DATA/pairing" "$DATA/logs"
if [[ "$SOURCE" != "$(cd "$PREFIX" && pwd)" ]]; then
  # The program files are replaced; hackeros-server.json (the configuration) is kept.
  find "$PREFIX" -mindepth 1 -maxdepth 1 ! -name hackeros-server.json -exec rm -rf {} +
  (cd "$SOURCE" && tar -cf - --exclude=./hackeros-server.json .) | (cd "$PREFIX" && tar -xf -)
fi
chmod +x "$PREFIX/HackerOs.Server"

if [[ "$SYSTEM" == "Darwin" ]]; then
  # Unsigned binaries (ADR 0191 §4): drop the download quarantine and sign ad hoc, which Apple
  # silicon requires before it runs any native code.
  xattr -dr com.apple.quarantine "$PREFIX" 2> /dev/null || true
  codesign --force --sign - "$PREFIX/HackerOs.Server" > /dev/null 2>&1 || true
fi

# ── Local network certificate (ADR 0191 §2, step 2) ──────────────────────────
HOSTS="localhost;127.0.0.1;[::1]"
# ${LAN_NAMES[@]+"${LAN_NAMES[@]}"}: an empty array is an error under set -u in the bash 3.2 of macOS.
LAN_NAMES=()
if [[ -n "$LAN" ]]; then
  command -v openssl > /dev/null || fail "openssl is required for --lan"
  IFS=',' read -r -a LAN_NAMES <<< "$LAN"
  CERTS="$DATA/certs"
  mkdir -p "$CERTS"
  SAN="DNS:localhost,IP:127.0.0.1,IP:::1"
  for name in ${LAN_NAMES[@]+"${LAN_NAMES[@]}"}; do
    [[ "$name" =~ ^[A-Za-z0-9.:-]+$ ]] || fail "invalid --lan name '$name'"
    if [[ "$name" =~ ^[0-9.]+$ || "$name" == *:* ]]; then SAN="$SAN,IP:$name"; else SAN="$SAN,DNS:$name"; fi
    [[ "$name" == *:* ]] && HOSTS="$HOSTS;[$name]" || HOSTS="$HOSTS;$name"
  done

  echo "Generating the local certificate authority and the HTTPS certificate in $CERTS"
  openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes \
    -keyout "$CERTS/ca.key" -out "$CERTS/hackeros-local-ca.crt" \
    -subj "/CN=HackerOS Local CA ($(hostname))" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" 2> /dev/null
  openssl req -newkey rsa:2048 -sha256 -nodes \
    -keyout "$CERTS/server.key" -out "$CERTS/server.csr" -subj "/CN=${LAN_NAMES[0]}" 2> /dev/null
  printf 'subjectAltName=%s\nbasicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n' "$SAN" > "$CERTS/server.ext"
  # 825 days: the longest validity Apple platforms accept for a server certificate.
  openssl x509 -req -in "$CERTS/server.csr" -CA "$CERTS/hackeros-local-ca.crt" -CAkey "$CERTS/ca.key" \
    -CAcreateserial -days 825 -sha256 -extfile "$CERTS/server.ext" -out "$CERTS/server.crt" 2> /dev/null
  # The authority signs this one certificate only: its key is deleted, so nothing else can ever be
  # signed with an authority the devices trust. Running --lan again makes a new authority.
  rm -f "$CERTS/ca.key" "$CERTS/server.csr" "$CERTS/server.ext" "$CERTS/hackeros-local-ca.srl"
  chmod 600 "$CERTS/server.key"
  RECONFIGURE=1
fi

# ── Configuration (hackeros-server.json, read after appsettings.json) ────────
CONFIG="$PREFIX/hackeros-server.json"
if [[ ! -f "$CONFIG" || $RECONFIGURE -eq 1 ]]; then
  ORIGINS="\"$PAGES_ORIGIN\""
  ENDPOINTS="\"Local\": { \"Url\": \"http://localhost:$PORT\" }"
  CA_SETTING=""
  if [[ ${#LAN_NAMES[@]} -gt 0 ]]; then
    for name in ${LAN_NAMES[@]+"${LAN_NAMES[@]}"}; do
      [[ "$name" == *:* ]] && host="[$name]" || host="$name"
      ORIGINS="$ORIGINS, \"https://$host:$HTTPS_PORT\""
    done
    ENDPOINTS="$ENDPOINTS,
      \"Lan\": {
        \"Url\": \"https://*:$HTTPS_PORT\",
        \"Certificate\": { \"Path\": \"$DATA/certs/server.crt\", \"KeyPath\": \"$DATA/certs/server.key\" }
      }"
    CA_SETTING="\"LocalServer\": { \"CaCertificatePath\": \"$DATA/certs/hackeros-local-ca.crt\" },"
  fi
  cat > "$CONFIG" <<EOF
{
  "AllowedHosts": "$HOSTS",
  "Kestrel": {
    "Endpoints": {
      $ENDPOINTS
    }
  },
  "Cors": { "AllowedOrigins": [ $ORIGINS ] },
  "ConnectionStrings": { "HackerOsDb": "Data Source=$DATA/hackeros.db" },
  "BlobStorage": { "Root": "$DATA/blobs" },
  "ServerBackup": { "Root": "$DATA/backups" },
  "HackerOs": {
    $CA_SETTING
    "Pairing": { "Root": "$DATA/pairing" }
  }
}
EOF
  echo "Wrote $CONFIG"
else
  echo "Kept the existing $CONFIG (--reconfigure rewrites it)"
fi

chown -R "$RUN_USER" "$DATA"
chmod 750 "$DATA"

# ── First pairing code (ADR 0191 §6) ─────────────────────────────────────────
run_as() {
  if [[ "$(id -un)" == "$RUN_USER" ]]; then "$@"; else sudo -u "$RUN_USER" "$@"; fi
}
PAIR_OUTPUT="$(cd "$PREFIX" && run_as ./HackerOs.Server pair "$ACCOUNT" 2> /dev/null)" || fail "could not create the pairing code"

# ── Service ──────────────────────────────────────────────────────────────────
if [[ $SERVICE -eq 1 ]]; then
  if [[ "$SYSTEM" == "Linux" ]]; then
    cat > "/etc/systemd/system/$SERVICE_NAME.service" <<EOF
[Unit]
Description=HackerOS Server (network relay, accounts and sync for HackerOS)
Documentation=https://github.com/Kazo-ca/HackerOs/blob/main/doc/en/wasm/dev/installable-server.md
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=$RUN_USER
WorkingDirectory=$PREFIX
ExecStart=$PREFIX/HackerOs.Server
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=$DATA
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    systemctl enable --now "$SERVICE_NAME"
  else
    PLIST="/Library/LaunchDaemons/$LAUNCHD_LABEL.plist"
    cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LAUNCHD_LABEL</string>
  <key>ProgramArguments</key><array><string>$PREFIX/HackerOs.Server</string></array>
  <key>WorkingDirectory</key><string>$PREFIX</string>
  <key>UserName</key><string>$RUN_USER</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>$DATA/logs/hackeros-server.log</string>
  <key>StandardErrorPath</key><string>$DATA/logs/hackeros-server.log</string>
</dict>
</plist>
EOF
    chmod 644 "$PLIST"
    launchctl bootstrap system "$PLIST"
  fi
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo
echo "HackerOS Server is installed."
echo "  On this computer:   http://localhost:$PORT"
for name in ${LAN_NAMES[@]+"${LAN_NAMES[@]}"}; do
  [[ "$name" == *:* ]] && host="[$name]" || host="$name"
  echo "  Local network:      https://$host:$HTTPS_PORT"
done
if [[ ${#LAN_NAMES[@]} -gt 0 ]]; then
  echo "  Certificate to approve on each other device: $DATA/certs/hackeros-local-ca.crt"
  echo "  (also at http://localhost:$PORT/api/hackeros/local-ca.crt)"
fi
if [[ $SERVICE -eq 0 ]]; then
  echo "  No service registered: start it with  cd $PREFIX && ./HackerOs.Server"
fi
echo
echo "$PAIR_OUTPUT"
echo "Another code later:  cd $PREFIX && sudo -u $RUN_USER ./HackerOs.Server pair $ACCOUNT"
