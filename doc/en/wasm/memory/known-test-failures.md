> [Version française](../../../fr/wasm/memory/known-test-failures.md)

# Known pre-existing test failures

State as of **2026-10-30**. These tests fail or are flaky on `main` with no
link to any PR. A PR that sees them fail **names** them in its Tests section;
it does not disable, skip or quarantine them. If one of these tests passes
reliably again, remove it from this list. Causes already found and fixed are
in [`../dev/pitfalls.md`](../dev/pitfalls.md) §4.

## Fail every time (Linux cloud container)

| Suite | Test | Symptom |
| --- | --- | --- |
| `Kazo.Os.E2E.Tests` | `Window_runtime_renders_and_handles_mouse_and_touch_pointer_gestures`, `Window_runtime_handles_every_resize_edge_in_real_browser` | Time-out; still there after ADR 0177. |
| `Kazo.Os.UI.E2E.Tests` | `Every_listed_surface_has_no_serious_axe_violation…`, `Representative_real_app_surfaces…` (axe) | `label-content-name-mismatch` on the taskbar's `.launcher-trigger` and `.taskbar-clock`. |
| `Kazo.Os.TrimmedPublish.E2E.Tests` | Calculator test (axe) | `label-content-name-mismatch` on the MC, MR, M+, M-, AC, x² keys. |
| `Kazo.Os.UI.E2E.Tests` | `LowStorageSpaceTests.A_full_quota_warns_once…` | "No low-space notification within 60 s". |

## Flaky (pass alone, sometimes fail under load)

- `AppInstallationService` (unit tests);
- `LocalNetworkTests` with a web worker;
- the detached-window E2E harness;
- `Kazo.Os.UI.E2E.Tests`: `FileExplorerDownload` and `BrowserHomePage` when
  the whole suite runs;
- `Kazo.Os.Platform.WorkerHost.Tests`: one timing test.

CI reruns a failed test once and reports "Flaky test" if it passes on the
second try ([`../dev/ci-workflows.md`](../dev/ci-workflows.md)).

## Recent environment tips

They complete [`../dev/pitfalls.md`](../dev/pitfalls.md) and
[`../dev/building-and-testing.md`](../dev/building-and-testing.md).

- The containers' `/usr/bin/dotnet` SDK (10.0.114 on 2026-10-30) still
  mis-compiles `ResponseBodyViewer.razor`. The Microsoft 10.0.403 SDK in
  `~/.dotnet`, first in `PATH` with `DOTNET_ROOT`, works.
- If the sandbox refuses `curl … dotnet-install.sh | bash`, read the SDK
  archive URL and its `sha512` from
  `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`,
  check the hash, then extract into `~/.dotnet`.
- In a container, headless Chromium crashes when reading an OPFS handle back
  from IndexedDB: a Playwright test of reconnecting a network drive
  (ADR 0181) is not possible there.
- To reproduce a flaky timing test, load the CPU
  (`for j in 1 2 3 4; do (timeout 300 sh -c 'while :; do :; done' &); done`)
  then rerun the test in a loop with `dotnet test --no-build --filter …`.
- Mobile home: there are two Settings buttons (`.mobile-home-screen` and the
  dock); in Playwright, take `.first()`.
