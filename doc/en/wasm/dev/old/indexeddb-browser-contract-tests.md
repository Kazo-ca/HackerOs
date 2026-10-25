# IndexedDB Browser Contract Tests

## Purpose

Prove that Browser Infrastructure repositories execute against native Chromium
IndexedDB rather than only against unit-test JavaScript fakes. This test harness
is independent of the future HackerOS product host.

## Architecture

`Tests/HackerOs.BrowserHarness.Tests` is a minimal Blazor WebAssembly executable.
It references Browser Infrastructure, loads its published
`_content/HackerOs.Infrastructure.Browser/indexedDb.js` asset, and reports a
machine-readable `running`, `passed`, or `failed` state in the DOM.

`Tests/HackerOs.E2E.Tests` is an xUnit project using Microsoft Playwright. The
test allocates a local port, starts the harness DevServer, launches installed
Chrome headlessly, waits for the terminal contract state, and always terminates
the server process. It requires Chrome and does not download a browser during a
test run.

## Covered Contracts

- Local groups create/find round trip and missing lookup.
- Canonical settings initialization, read, revisioned write, stale conflict,
  and committed-content readback.
- Stable filesystem root initialization and idempotent reinitialization.
- Filesystem stat, create, enumerate, rename, delete, and absence checks using
  observed optimistic revisions.
- Real static-web-asset import and native IndexedDB schema migration to version
  2.
- Page reload retains committed settings revision and filesystem entries.
- A duplicate failure midway through a multi-write transaction rolls back all
  earlier writes.
- A failed version upgrade retains the prior committed schema and records.
- Chromium quota override produces native `QuotaExceededError`, which crosses
  JS interop as recoverable `BrowserStorageQuotaException`.
- Versioned backup replacement removes post-backup data and identical merge is
  idempotent.
- Public maintenance removes an aged unreferenced content chunk.
- Two tabs racing one expected revision produce exactly one commit and one
  conflict.

The real-browser matrix exposed and fixed two interop defects that unit fakes
did not enforce: inline-key writes incorrectly passed a separate `null` key to
`IDBObjectStore.add`/`put`, and rejected DOM exceptions reached Blazor as
`JSException: undefined` until normalized to named JavaScript errors.

## Usage

Run the browser contract directly:

```powershell
dotnet test Tests/HackerOs.E2E.Tests/HackerOs.E2E.Tests.csproj
```

The project is also part of `HackerOs.sln`, so the full solution test command
includes the Chromium contract.

## Boundaries

This slice proves `P2-IDB-013` and `P2-IDB-014` in installed Chromium. Firefox
and Safari automation remain a recorded gap under ADR 0015.

## Completed Task List

- [x] Build a host-independent Blazor WASM browser harness.
- [x] Publish and execute the real IndexedDB static web asset.
- [x] Run group and settings contracts in Chrome.
- [x] Run core filesystem contracts in Chrome.
- [x] Integrate the Playwright test into the solution.
- [x] Prove reload, rollback, failed migration, and quota behavior.
- [x] Prove backup/restore, cleanup, and multi-tab revision conflict.
- [x] Document browser and scenario boundaries.