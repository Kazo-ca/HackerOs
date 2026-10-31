#!/usr/bin/env bash
# publish-trimmed.sh — trimmed Release publish of the Ecosystem host and its E2E test (ADR 0106 §5,
# ADR 0113).
#
# One command, run from anywhere:
#
#   wasm2/HackerOs/build/publish-trimmed.sh [--skip-build] [--compare]
#
# It publishes OS/HackerOs.Ecosystem in Release (PublishTrimmed=true, TrimMode=partial, every
# HackerOS assembly kept whole), prints the size of the published .wasm files, serves the output
# with a static file server and runs Tests/HackerOs.TrimmedPublish.E2E.Tests against it: sign-in,
# every window app of the Start menu, a pipeline in the worker pool, the Intranet and the sample
# Blazor site in the Browser, then a reload with no network once the service worker has cached the
# release. Exit code 0 only when the publish and every test (EXPECTED_TESTS) passed.
#
#   --skip-build      reuse the existing publish and test build
#   --compare         also publish untrimmed (-p:PublishTrimmed=false) and print both sizes
#
# The service worker is only registered off localhost and 127.0.0.1 (index.html), so the offline
# test uses a second server on 127.0.0.2 (a loopback address Chromium treats as secure). Where that
# address cannot be bound, the offline test reports as skipped and the run says so.
#
# Output goes to wasm2/HackerOs/artifacts/publish-trimmed/ (ignored by git). Prerequisites: .NET SDK
# 10, python3, curl, and a Chromium Playwright can launch (in a cloud container,
# /opt/pw-browsers/chromium is used automatically — see doc/en/wasm/dev/building-and-testing.md).

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/publish-trimmed"
LOGS="$OUT/logs"
HOST_PROJECT="$ROOT/OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj"
TEST_PROJECT="$ROOT/Tests/HackerOs.TrimmedPublish.E2E.Tests/HackerOs.TrimmedPublish.E2E.Tests.csproj"
CATALOG="$ROOT/OS/HackerOs.Ecosystem/obj/Release/net10.0/BuiltInApplications.json"

SKIP_BUILD=0
COMPARE=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-build) SKIP_BUILD=1 ;;
        --compare) COMPARE=1 ;;
        -h|--help) sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
    shift
done

if [[ -d "$HOME/.dotnet" && ":$PATH:" != *":$HOME/.dotnet:"* ]]; then
    export PATH="$HOME/.dotnet:$PATH"
fi
for tool in dotnet python3 curl setsid; do
    command -v "$tool" >/dev/null || { echo "publish-trimmed: '$tool' not found on PATH" >&2; exit 2; }
done

# Known container workarounds (doc/en/wasm/dev/building-and-testing.md): NuGet audit advisories on build-only
# packages, and the Windows-style local feed path of the windowing sample host.
export NuGetAudit=false
export MSBUILDDISABLENODEREUSE=1
mkdir -p "$ROOT/artifacts/local-nupkg" "$ROOT/Samples/HackerOs.Windowing.SampleHost/..\\..\\artifacts\\local-nupkg"
mkdir -p "$LOGS"

log() { echo "[publish-trimmed] $*"; }

SERVER_PIDS=()
stop_servers() {
    for pid in "${SERVER_PIDS[@]}"; do
        kill -TERM -- "-$pid" 2>/dev/null
        wait "$pid" 2>/dev/null
    done
    SERVER_PIDS=()
}
trap stop_servers EXIT
trap 'stop_servers; exit 130' INT TERM

free_port() {
    python3 -c 'import socket,sys; s=socket.socket(); s.bind((sys.argv[1],0)); print(s.getsockname()[1]); s.close()' "${1:-127.0.0.1}"
}

# The SDK converts each assembly to WebCIL incrementally, by timestamp: a publish that follows one
# with the other PublishTrimmed value can reuse the other's converted files (an untrimmed publish
# then ships trimmed assemblies). Each publish here starts from a clean link/convert state.
HOST_OBJ="$ROOT/OS/HackerOs.Ecosystem/obj/Release/net10.0"
clean_link_state() { rm -rf "$HOST_OBJ/linked" "$HOST_OBJ/webcil" "$HOST_OBJ/tmp-webcil"; }

# wasm_size DIR — total bytes of the published .wasm files (satellite assemblies included), and of
# their Brotli copies.
wasm_size() {
    python3 - "$1/wwwroot/_framework" <<'PY'
import os, sys
files = [os.path.join(d, n) for d, _, names in os.walk(sys.argv[1]) for n in names]
raw = sum(os.path.getsize(f) for f in files if f.endswith(".wasm"))
br = sum(os.path.getsize(f) for f in files if f.endswith(".wasm.br"))
count = sum(1 for f in files if f.endswith(".wasm"))
print(f"{count} .wasm files, {raw / 1048576:.1f} MiB, {br / 1048576:.1f} MiB Brotli")
PY
}

# Static file server for a published wwwroot, with the MIME types Blazor WebAssembly needs.
STATIC_SERVER="$OUT/static_server.py"
cat >"$STATIC_SERVER" <<'PY'
import functools, http.server, sys
root, port, address = sys.argv[1], int(sys.argv[2]), sys.argv[3]
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
http.server.ThreadingHTTPServer((address, port), functools.partial(Handler, directory=root)).serve_forever()
PY

# serve ADDRESS PORT LOGFILE — starts the static server and waits until it answers.
serve() {
    local address="$1" port="$2" logfile="$3"
    setsid python3 "$STATIC_SERVER" "$OUT/publish/wwwroot" "$port" "$address" >"$logfile" 2>&1 &
    SERVER_PIDS+=($!)
    for _ in $(seq 1 30); do
        [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://$address:$port/")" == "200" ]] && return 0
        sleep 0.5
    done
    return 1
}

cd "$ROOT" || exit 2

# --------------------------------------------------------------------------------------------------
# 1. Publish and build the test
# --------------------------------------------------------------------------------------------------
if [[ $SKIP_BUILD -eq 0 ]]; then
    if [[ $COMPARE -eq 1 ]]; then
        log "publishing OS/HackerOs.Ecosystem (Release, untrimmed) for comparison"
        rm -rf "$OUT/publish-untrimmed"
        clean_link_state
        dotnet publish "$HOST_PROJECT" -c Release -p:PublishTrimmed=false -o "$OUT/publish-untrimmed" \
            >"$LOGS/publish-untrimmed.log" 2>&1 || { log "untrimmed publish failed (log: ${LOGS#"$ROOT/"}/publish-untrimmed.log)"; exit 1; }
    fi
    log "publishing OS/HackerOs.Ecosystem (Release, trimmed)"
    rm -rf "$OUT/publish"
    clean_link_state
    if ! dotnet publish "$HOST_PROJECT" -c Release -o "$OUT/publish" >"$LOGS/publish.log" 2>&1; then
        grep -E ' error |error [A-Z]+[0-9]+' "$LOGS/publish.log" | sort -u | head -20 | sed 's/^/    /'
        log "RESULT: FAIL (publish, log: ${LOGS#"$ROOT/"}/publish.log)"
        exit 1
    fi
    # ILLink warnings are errors (TreatWarningsAsErrors); none may be left as a warning either.
    if grep -qE 'warning IL[0-9]+' "$LOGS/publish.log"; then
        grep -E 'warning IL[0-9]+' "$LOGS/publish.log" | sort -u | head -20 | sed 's/^/    /'
        log "RESULT: FAIL (trim warnings in the publish)"
        exit 1
    fi
    log "building the test suite"
    if ! dotnet build "$TEST_PROJECT" -c Debug >"$LOGS/build-tests.log" 2>&1; then
        grep -E ' error ' "$LOGS/build-tests.log" | sort -u | head -20 | sed 's/^/    /'
        log "RESULT: FAIL (test build)"
        exit 1
    fi
fi

[[ -d "$OUT/publish/wwwroot/_framework" ]] || { log "no publish in ${OUT#"$ROOT/"}/publish; run without --skip-build"; exit 1; }
log "trimmed:   $(wasm_size "$OUT/publish")"
if [[ -d "$OUT/publish-untrimmed/wwwroot/_framework" ]]; then
    log "untrimmed: $(wasm_size "$OUT/publish-untrimmed")"
fi

# --------------------------------------------------------------------------------------------------
# 2. Serve and test
# --------------------------------------------------------------------------------------------------
PORT="$(free_port 127.0.0.1)"
serve 127.0.0.1 "$PORT" "$LOGS/server.log" || { log "RESULT: FAIL (static server did not start)"; exit 1; }
PWA_URL=""
if PWA_PORT="$(free_port 127.0.0.2 2>/dev/null)" && serve 127.0.0.2 "$PWA_PORT" "$LOGS/server-pwa.log"; then
    PWA_URL="http://127.0.0.2:$PWA_PORT"
else
    log "127.0.0.2 cannot be bound: the offline test will be skipped"
fi
log "serving on http://127.0.0.1:$PORT${PWA_URL:+ and $PWA_URL}"

HACKEROS_TRIMMED_URL="http://127.0.0.1:$PORT" \
HACKEROS_TRIMMED_PWA_URL="$PWA_URL" \
HACKEROS_TRIMMED_CATALOG="$CATALOG" \
HACKEROS_TRIMMED_ARTIFACTS="$OUT/screenshots" \
    dotnet test "$TEST_PROJECT" --no-build -c Debug --logger "console;verbosity=detailed" >"$LOGS/tests.log" 2>&1
code=$?
stop_servers
grep -E '^\s*\[(trimmed|chromium)\]' "$LOGS/tests.log" | sed "s/^ */    /"

# A skipped test exits 0 too: require every test to have actually run and passed. Five tests: the
# boot with every app, the offline boot, the window rendered in a worker with its measurements
# (ADR 0133, TrimmedPublishTests.RemoteWindows.cs), and the unread notifications dot next to the
# clock (ADR 0196, TrimmedPublishTests.Notifications.cs).
EXPECTED_TESTS=5
if [[ $code -eq 0 ]] && grep -qE "^\s*Passed: +$EXPECTED_TESTS\s*$" "$LOGS/tests.log" && ! grep -qE '^\s*Skipped: +[1-9]' "$LOGS/tests.log"; then
    [[ -z "$PWA_URL" ]] && log "note: the offline test did not run (no 127.0.0.2)"
    log "RESULT: PASS"
    exit 0
fi
grep -E 'Error Message|Assert|Exception|Console errors|console:|page error' -A3 "$LOGS/tests.log" | head -30 | sed "s/^ */    /"
log "RESULT: FAIL (log: ${LOGS#"$ROOT/"}/tests.log, screenshots in ${OUT#"$ROOT/"}/screenshots)"
exit 1
