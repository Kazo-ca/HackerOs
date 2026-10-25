#!/usr/bin/env bash
# verify-hosts.sh — boot smoke test of the three HackerOS hosts (execution-isolation-plan.md §5.2).
#
# One command, run from anywhere:
#
#   wasm2/HackerOs/build/verify-hosts.sh [--skip-build] [--only NAME[,NAME...]] [--keep-artifacts]
#
# It builds the three hosts and the smoke-test suite, publishes OS/HackerOs.Ecosystem, starts each
# host (the published Ecosystem behind a static file server, test/test and HackerOs.Server through
# `dotnet run`, each on a free loopback port), runs Tests/HackerOs.HostSmoke.Tests against it, stops
# it, and prints one summary line per step. Exit code 0 only when every step passed.
#
# Runs (names usable with --only):
#   ecosystem               published PWA, default build and settings (measurements on)
#   ecosystem-build-off     published with -p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false
#   ecosystem-settings-off  default publish, wwwroot/appsettings.json switching both measurements off
#   ecosystem-limits-off    default publish, wwwroot/appsettings.json switching the process limits off (ADR 0125)
#   ecosystem-oom           default publish, wwwroot/appsettings.json lowering the simulated MemTotal to
#                           OOM_MEMORY_MIB (512 by default)
#                           (HackerOs:Hardware:MemoryMiB); the smoke test turns the OOM killer on with sudo,
#                           runs stress --vm and expects the kill in dmesg (ADR 0126)
#   ecosystem-isolated      default publish served on 127.0.0.2: a secure context where index.html registers
#                           the PWA service worker (it skips localhost and 127.0.0.1), which adds COOP/COEP; the
#                           page must be cross-origin isolated, with shared pipes and synchronous calls (ADR 0131)
#   ecosystem-isolation-off same on 127.0.0.2, appsettings.json setting HackerOs:CrossOriginIsolation to "off":
#                           the service worker adds nothing and the page must not be isolated
#   test-host               test/test (Blazor Web App, InteractiveWebAssembly; its middleware sends COOP/COEP)
#   server                  Server/HackerOs.Server (serves the Ecosystem's OS in WebAssembly and its API, ADR 0130;
#                           the OS must connect to it by itself, ADR 0134; its middleware sends COOP/COEP)
#
# Cross-origin isolation (ADR 0131, 0143): the runs on 127.0.0.1 behind the static server get no
# header (no service worker there) and must report "cross-origin-isolated: no"; the two dotnet hosts
# and ecosystem-isolated must report "yes". A run that edits appsettings.json re-hashes its entry in
# service-worker-assets.js, otherwise the service worker's install fails its integrity check.
#
# The two "-off" runs need the ADR 0066 switches (build/HackerOs.ResourceAccounting.targets and the
# HackerOs:ResourceAccounting section of appsettings.json). On a tree without them they are reported
# as SKIP, never as a failure.
#
# Logs, published output and failure screenshots go to wasm2/HackerOs/artifacts/verify-hosts/
# (ignored by git). Prerequisites: .NET SDK 10, python3, curl, and a Chromium Playwright can launch
# (in a cloud container, /opt/pw-browsers/chromium is used automatically — see
# doc/en/wasm/dev/building-and-testing.md).

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/verify-hosts"
LOGS="$OUT/logs"
SMOKE_PROJECT="$ROOT/Tests/HackerOs.HostSmoke.Tests/HackerOs.HostSmoke.Tests.csproj"
ACCOUNTING_TARGETS="$ROOT/build/HackerOs.ResourceAccounting.targets"
BOOT_TIMEOUT_S=180

SKIP_BUILD=0
KEEP=0
ONLY=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-build) SKIP_BUILD=1 ;;
        --keep-artifacts) KEEP=1 ;;
        --only) ONLY="${2:-}"; shift ;;
        -h|--help) sed -n '2,43p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
    shift
done

if [[ -d "$HOME/.dotnet" && ":$PATH:" != *":$HOME/.dotnet:"* ]]; then
    export PATH="$HOME/.dotnet:$PATH"
fi
for tool in dotnet python3 curl setsid; do
    command -v "$tool" >/dev/null || { echo "verify-hosts: '$tool' not found on PATH" >&2; exit 2; }
done

# Known container workarounds (doc/en/wasm/dev/building-and-testing.md): NuGet audit advisories on build-only
# packages, and the local feed directory required by the windowing sample host.
export NuGetAudit="${NuGetAudit:-false}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p "$ROOT/artifacts/local-nupkg"

[[ $KEEP -eq 1 ]] || rm -rf "$OUT"
mkdir -p "$LOGS"

declare -a SUMMARY=()
FAILED=0
HOST_PID=""

log() { printf '[verify-hosts] %s\n' "$*"; }

record() { # record NAME STATUS DETAIL
    SUMMARY+=("$(printf '%-32s %-5s %s' "$1" "$2" "$3")")
    [[ "$2" == "FAIL" ]] && FAILED=1
    return 0
}

wanted() { [[ -z "$ONLY" || ",$ONLY," == *",$1,"* ]]; }

free_port() {
    python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()'
}

# Stops the current host's whole process group (dotnet run starts the app as a child): SIGTERM,
# then SIGKILL after 15 s.
stop_host() {
    [[ -n "$HOST_PID" ]] || return 0
    if kill -0 "$HOST_PID" 2>/dev/null; then
        kill -TERM -- "-$HOST_PID" 2>/dev/null
        for _ in $(seq 1 30); do kill -0 "$HOST_PID" 2>/dev/null || break; sleep 0.5; done
        kill -KILL -- "-$HOST_PID" 2>/dev/null
    fi
    wait "$HOST_PID" 2>/dev/null
    HOST_PID=""
}
trap stop_host EXIT
trap 'stop_host; exit 130' INT TERM

# start_host NAME LOGFILE COMMAND... — runs the command in its own process group.
start_host() {
    local name="$1" logfile="$2"; shift 2
    setsid "$@" >"$logfile" 2>&1 &
    HOST_PID=$!
    log "$name: started (pid $HOST_PID)"
}

# wait_ready URL — until the host answers 200 on its root, or it dies, or the timeout expires.
wait_ready() {
    local url="$1" deadline=$((SECONDS + BOOT_TIMEOUT_S))
    while (( SECONDS < deadline )); do
        kill -0 "$HOST_PID" 2>/dev/null || return 1
        [[ "$(curl -s -o /dev/null -w '%{http_code}' "$url/")" == "200" ]] && return 0
        sleep 1
    done
    return 1
}

# expected_isolation NAME — "yes" or "no": crossOriginIsolated the run must see (ADR 0131), or ""
# on a tree without the setting.
expected_isolation() {
    grep -q '"CrossOriginIsolation"' "$ROOT/OS/HackerOs.Ecosystem/wwwroot/appsettings.json" 2>/dev/null || { echo ""; return; }
    case "$1" in
        ecosystem-isolated|test-host|server) echo yes ;;
        *) echo no ;;
    esac
}

# run_smoke NAME URL [CPU_SOURCE MEM_SOURCE] — runs the Playwright suite against a running host.
# A run whose name ends in "-limits-off" expects the ADR 0125 limits off.
run_smoke() {
    local name="$1" url="$2" cpu="${3:-}" mem="${4:-}" logfile="$LOGS/$1.smoke.log" limits=on oom=off
    [[ "$name" == *-limits-off ]] && limits=off
    [[ "$name" == *-oom ]] && oom=on
    HACKEROS_SMOKE_URL="$url" HACKEROS_SMOKE_HOST="$name" HACKEROS_SMOKE_EXPECT_LIMITS="$limits" HACKEROS_SMOKE_EXPECT_OOM="$oom" \
    HACKEROS_SMOKE_EXPECT_ISOLATION="$(expected_isolation "$name")" \
    HACKEROS_SMOKE_EXPECT_CPU_SOURCE="$cpu" HACKEROS_SMOKE_EXPECT_MEM_SOURCE="$mem" \
    HACKEROS_SMOKE_ARTIFACTS="$OUT/screenshots" \
        dotnet test "$SMOKE_PROJECT" --no-build -c Debug --logger "console;verbosity=detailed" >"$logfile" 2>&1
    local code=$?
    grep -E '^\s*\[(smoke|chromium)\]' "$logfile" | sed "s/^ */    /"
    # A skipped test exits 0 too: require one test actually run and passed.
    if [[ $code -eq 0 ]] && grep -qE '^\s*Passed: +1\s*$' "$logfile" && ! grep -qE '^\s*Skipped: +[1-9]' "$logfile"; then
        return 0
    fi
    grep -E 'Error Message|Assert|Exception|Console errors|console:|page error' "$logfile" | head -20 | sed "s/^ */    /"
    return 1
}

# verify_run NAME URL LOGFILE EXPECT_CPU EXPECT_MEM COMMAND... — start, smoke, stop, record.
verify_run() {
    local name="$1" url="$2" hostlog="$3" cpu="$4" mem="$5"; shift 5
    local started=$SECONDS
    start_host "$name" "$hostlog" "$@"
    if ! wait_ready "$url"; then
        log "$name: did not answer on $url within ${BOOT_TIMEOUT_S}s"; tail -20 "$hostlog" | sed 's/^/    /'
        stop_host
        record "$name" FAIL "host did not start (log: ${hostlog#"$ROOT/"})"
        return
    fi
    log "$name: listening on $url, running the smoke test"
    if run_smoke "$name" "$url" "$cpu" "$mem"; then
        stop_host
        # Server-side failures never reach the browser console (an API call the OS made, the
        # automatic connection of ADR 0134): an ASP.NET Core "fail:" entry fails the run as well.
        local host_failures
        host_failures="$(grep -cE '^fail: |Unhandled exception' "$hostlog")"
        if [[ "$host_failures" -gt 0 ]]; then
            grep -E -A3 '^fail: |Unhandled exception' "$hostlog" | head -20 | sed 's/^/    /'
            record "$name" FAIL "$host_failures error(s) in the host log (${hostlog#"$ROOT/"})"
        else
            record "$name" PASS "$((SECONDS - started))s"
        fi
    else
        stop_host
        record "$name" FAIL "smoke test failed (log: ${LOGS#"$ROOT/"}/$name.smoke.log)"
    fi
}

# Static file server for a published wwwroot, with the MIME types Blazor WebAssembly needs.
STATIC_SERVER="$OUT/static_server.py"
cat >"$STATIC_SERVER" <<'PY'
import functools, http.server, sys
root, port = sys.argv[1], int(sys.argv[2])
bind = sys.argv[3] if len(sys.argv) > 3 else "127.0.0.1"
class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map,
                      ".wasm": "application/wasm", ".js": "text/javascript", ".mjs": "text/javascript",
                      ".json": "application/json", ".webmanifest": "application/manifest+json",
                      ".dat": "application/octet-stream", ".blat": "application/octet-stream"}
    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()
    def log_message(self, fmt, *args):
        sys.stderr.write("%s\n" % (fmt % args))
http.server.ThreadingHTTPServer((bind, port), functools.partial(Handler, directory=root)).serve_forever()
PY

# rehash_settings DIR — after a run edits DIR/wwwroot/appsettings.json: drops its stale compressed
# copies and updates its hash in service-worker-assets.js (the service worker installs every asset
# with its integrity; a stale hash fails the install, which only matters off 127.0.0.1).
rehash_settings() {
    local wwwroot="$1/wwwroot"
    rm -f "$wwwroot/appsettings.json.br" "$wwwroot/appsettings.json.gz" \
          "$wwwroot/service-worker-assets.js.br" "$wwwroot/service-worker-assets.js.gz"
    python3 - "$wwwroot" <<'PY'
import base64, hashlib, re, sys
root = sys.argv[1]
with open(f"{root}/appsettings.json", "rb") as f:
    digest = "sha256-" + base64.b64encode(hashlib.sha256(f.read()).digest()).decode()
path = f"{root}/service-worker-assets.js"
with open(path, encoding="utf-8") as f:
    text = f.read()
text, count = re.subn(r'"hash": "sha256-[^"]+",(\s*)"url": "appsettings\.json"', f'"hash": "{digest}",\\1"url": "appsettings.json"', text)
if count == 1:
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
PY
}

# --------------------------------------------------------------------------------------------------
# 1. Build
# --------------------------------------------------------------------------------------------------
HAS_SWITCHES=0
[[ -f "$ACCOUNTING_TARGETS" ]] && HAS_SWITCHES=1

# Expected CpuSource:/MemSource: values in /proc/self/status (ADR 0066 to 0068): measured by
# default on every host (HackerOs.Server serves the Ecosystem's own appsettings.json since ADR
# 0130), simulated in both "-off" runs. Empty means "not checked" (tree without ADR 0066).
DEFAULT_SOURCE=""
if [[ $HAS_SWITCHES -eq 1 ]]; then
    DEFAULT_SOURCE=measured
fi

build_step() { # build_step LABEL LOGFILE COMMAND...
    local label="$1" logfile="$2"; shift 2
    log "$label"
    if ! "$@" >"$logfile" 2>&1; then
        grep -E ' error |error [A-Z]+[0-9]+' "$logfile" | sort -u | head -20 | sed 's/^/    /'
        record "$label" FAIL "log: ${logfile#"$ROOT/"}"
        return 1
    fi
    record "$label" PASS ""
    return 0
}

cd "$ROOT" || exit 2
if [[ $SKIP_BUILD -eq 0 ]]; then
    for project in OS/HackerOs.Ecosystem test/test Server/HackerOs.Server Tests/HackerOs.HostSmoke.Tests; do
        name="$(basename "$project")"
        build_step "build $name" "$LOGS/build-$name.log" dotnet build "$project" -c Debug || true
    done
fi

if wanted ecosystem || wanted ecosystem-settings-off || wanted ecosystem-limits-off || wanted ecosystem-oom \
    || wanted ecosystem-isolated || wanted ecosystem-isolation-off; then
    build_step "publish ecosystem" "$LOGS/publish-ecosystem.log" \
        dotnet publish OS/HackerOs.Ecosystem -c Release -o "$OUT/publish/ecosystem" || true
fi
if wanted ecosystem-build-off && [[ $HAS_SWITCHES -eq 1 ]]; then
    build_step "publish ecosystem-build-off" "$LOGS/publish-ecosystem-build-off.log" \
        dotnet publish OS/HackerOs.Ecosystem -c Release -o "$OUT/publish/ecosystem-build-off" \
            -p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false || true
fi

if [[ $FAILED -ne 0 ]]; then
    log "build failed; no host started."
else
# --------------------------------------------------------------------------------------------------
# 2. Runs
# --------------------------------------------------------------------------------------------------
    serve_published() { # serve_published NAME DIR EXPECT_CPU EXPECT_MEM [ADDRESS]
        local port address="${5:-127.0.0.1}"; port="$(free_port)"
        verify_run "$1" "http://$address:$port" "$LOGS/$1.host.log" "$3" "$4" \
            python3 "$STATIC_SERVER" "$2/wwwroot" "$port" "$address"
    }

    if wanted ecosystem; then
        serve_published ecosystem "$OUT/publish/ecosystem" "$DEFAULT_SOURCE" "$DEFAULT_SOURCE"
    fi

    if wanted ecosystem-build-off; then
        if [[ $HAS_SWITCHES -eq 0 ]]; then
            record ecosystem-build-off SKIP "no build/HackerOs.ResourceAccounting.targets (ADR 0066 not in this tree)"
        elif compgen -G "$OUT/publish/ecosystem-build-off/wwwroot/_framework/HackerOs.Platform.ResourceAccounting*" >/dev/null; then
            record ecosystem-build-off FAIL "measurement code still published with both switches off"
        else
            serve_published ecosystem-build-off "$OUT/publish/ecosystem-build-off" simulated simulated
        fi
    fi

    if wanted ecosystem-settings-off; then
        settings="$OUT/publish/ecosystem/wwwroot/appsettings.json"
        if [[ ! -f "$settings" ]] || ! grep -q '"ResourceAccounting"' "$settings"; then
            record ecosystem-settings-off SKIP "no HackerOs:ResourceAccounting in appsettings.json (ADR 0066 not in this tree)"
        else
            off="$OUT/publish/ecosystem-settings-off"
            rm -rf "$off" && cp -r "$OUT/publish/ecosystem" "$off"
            rm -f "$off/wwwroot/appsettings.json.br" "$off/wwwroot/appsettings.json.gz"
            python3 - "$off/wwwroot/appsettings.json" <<'PY'
import json, sys
path = sys.argv[1]
with open(path, encoding="utf-8-sig") as f:
    data = json.load(f)
accounting = data.setdefault("HackerOs", {}).setdefault("ResourceAccounting", {})
for kind in ("Cpu", "Memory"):
    accounting.setdefault(kind, {})["Enabled"] = False
with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2)
PY
            rehash_settings "$off"
            serve_published ecosystem-settings-off "$off" simulated simulated
        fi
    fi

    if wanted ecosystem-limits-off; then
        settings="$OUT/publish/ecosystem/wwwroot/appsettings.json"
        if [[ ! -f "$settings" ]] || ! grep -q '"ResourceLimits"' "$settings"; then
            record ecosystem-limits-off SKIP "no HackerOs:ResourceLimits in appsettings.json (ADR 0125 not in this tree)"
        else
            off="$OUT/publish/ecosystem-limits-off"
            rm -rf "$off" && cp -r "$OUT/publish/ecosystem" "$off"
            rm -f "$off/wwwroot/appsettings.json.br" "$off/wwwroot/appsettings.json.gz"
            python3 - "$off/wwwroot/appsettings.json" <<'PY'
import json, sys
path = sys.argv[1]
with open(path, encoding="utf-8-sig") as f:
    data = json.load(f)
data.setdefault("HackerOs", {}).setdefault("ResourceLimits", {})["Enabled"] = False
with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2)
PY
            rehash_settings "$off"
            serve_published ecosystem-limits-off "$off" "$DEFAULT_SOURCE" "$DEFAULT_SOURCE"
        fi
    fi

    # ADR 0126: a machine with little memory, so the OOM killer has something to do.
    if wanted ecosystem-oom; then
        settings="$OUT/publish/ecosystem/wwwroot/appsettings.json"
        if [[ ! -f "$settings" ]] || ! grep -q '"ResourceAccounting"' "$settings"; then
            record ecosystem-oom SKIP "no HackerOs:ResourceAccounting in appsettings.json (memory is not measured)"
        else
            oom="$OUT/publish/ecosystem-oom"
            rm -rf "$oom" && cp -r "$OUT/publish/ecosystem" "$oom"
            rm -f "$oom/wwwroot/appsettings.json.br" "$oom/wwwroot/appsettings.json.gz"
            python3 - "$oom/wwwroot/appsettings.json" "${OOM_MEMORY_MIB:-512}" <<'PY'
import json, sys
path, mebibytes = sys.argv[1], int(sys.argv[2])
with open(path, encoding="utf-8-sig") as f:
    data = json.load(f)
data.setdefault("HackerOs", {}).setdefault("Hardware", {})["MemoryMiB"] = mebibytes
with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2)
PY
            rehash_settings "$oom"
            serve_published ecosystem-oom "$oom" "$DEFAULT_SOURCE" "$DEFAULT_SOURCE"
        fi
    fi

    # ADR 0131: 127.0.0.2 is a secure context where the service worker registers and adds the headers.
    if wanted ecosystem-isolated; then
        serve_published ecosystem-isolated "$OUT/publish/ecosystem" "$DEFAULT_SOURCE" "$DEFAULT_SOURCE" 127.0.0.2
    fi

    if wanted ecosystem-isolation-off; then
        settings="$OUT/publish/ecosystem/wwwroot/appsettings.json"
        if [[ ! -f "$settings" ]] || ! grep -q '"CrossOriginIsolation"' "$settings"; then
            record ecosystem-isolation-off SKIP "no HackerOs:CrossOriginIsolation in appsettings.json (ADR 0131 not in this tree)"
        else
            off="$OUT/publish/ecosystem-isolation-off"
            rm -rf "$off" && cp -r "$OUT/publish/ecosystem" "$off"
            python3 - "$off/wwwroot/appsettings.json" <<'PY'
import json, sys
path = sys.argv[1]
with open(path, encoding="utf-8-sig") as f:
    data = json.load(f)
data.setdefault("HackerOs", {})["CrossOriginIsolation"] = "off"
with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2)
PY
            rehash_settings "$off"
            serve_published ecosystem-isolation-off "$off" "$DEFAULT_SOURCE" "$DEFAULT_SOURCE" 127.0.0.2
        fi
    fi

    # The dotnet run hosts: Development environment (common-pitfalls.md §4), and the server's
    # database, blobs and backups redirected to a throwaway directory so the tracked
    # Server/HackerOs.Server/hackeros.db is never touched.
    if wanted test-host; then
        port="$(free_port)"
        verify_run test-host "http://127.0.0.1:$port" "$LOGS/test-host.host.log" "$DEFAULT_SOURCE" "$DEFAULT_SOURCE" \
            env ASPNETCORE_ENVIRONMENT=Development \
            dotnet run --project test/test/test.csproj -c Debug --no-build --no-launch-profile --urls "http://127.0.0.1:$port"
    fi

    if wanted server; then
        port="$(free_port)"
        data="$OUT/server-data"; rm -rf "$data"; mkdir -p "$data"
        verify_run server "http://127.0.0.1:$port" "$LOGS/server.host.log" "$DEFAULT_SOURCE" "$DEFAULT_SOURCE" \
            env ASPNETCORE_ENVIRONMENT=Development \
                ConnectionStrings__HackerOsDb="Data Source=$data/hackeros.db" \
                BlobStorage__Root="$data/blobs" ServerBackup__Root="$data/backups" \
            dotnet run --project Server/HackerOs.Server/HackerOs.Server.csproj -c Debug --no-build --no-launch-profile --urls "http://127.0.0.1:$port"
    fi
fi

# --------------------------------------------------------------------------------------------------
# 3. Summary
# --------------------------------------------------------------------------------------------------
echo
echo "== HackerOS host verification ($(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo '?')) =="
printf '%s\n' "${SUMMARY[@]}"
if [[ $FAILED -ne 0 ]]; then
    echo "RESULT: FAIL (logs in ${OUT#"$ROOT/"})"
    exit 1
fi
echo "RESULT: PASS"
exit 0
