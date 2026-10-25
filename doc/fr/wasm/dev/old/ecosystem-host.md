# HackerOS Ecosystem Host

## Purpose

`HackerOs.Ecosystem` is the standalone .NET 10 Blazor WebAssembly PWA that will
compose the local HackerOS platform. The host remains a thin composition and boot
boundary; domain behavior belongs to Platform, Infrastructure, or app projects.

## Architecture

The project lives under `OS/HackerOs.Ecosystem` and uses the
`Microsoft.NET.Sdk.BlazorWebAssembly` SDK. It currently contains only a minimal
boot-critical Razor surface, scoped component styling, a web app manifest, and
the development/published service-worker pair generated for a standalone PWA.

Repository-wide trimming analysis and warnings-as-errors apply to the host. The
initial template router was removed because it triggered `IL2111` through the
reflection-based `NotFoundPage` parameter and routing is not required by the
initial scaffold.

`AddHackerOsEcosystem` owns the synchronous composition graph. Browser storage,
settings, capability persistence, filesystem, session, process simulation, app
lifecycle, intents, windows, notifications, clock, and diagnostics are
process-wide singletons. `FileSystemSeeder` is transient because it performs one
boot/login operation. File-dialog coordinators are created by
`FileDialogServiceFactory` only after a real `SessionId` exists. IndexedDB
reconciliation and filesystem/session initialization remain asynchronous boot
responsibilities.

`App.razor` now renders one explicit host state at a time: initialization,
first-run Administrator onboarding, login, desktop, storage recovery, or fatal
error. PWA update availability is modeled as a non-blocking overlay state. The
clean profile creates no default credentials; onboarding persists the group,
user, and PBKDF2 verifier through IndexedDB before activating a session.

Unhandled component failures cross `HostErrorBoundary`. The reporter assigns a
correlation ID, records a stable message plus exception type and phase, and never
persists the exception message or stack trace. Durable diagnostic failure falls
back to the bounded in-memory sink without replacing the original correlation.

## Usage

Build the host directly:

```powershell
dotnet build OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj
```

The project is also included in `HackerOs.sln` under the logical `OS` solution
folder.

## Key Decisions

- The host is standalone and requires no server.
- Template sample pages, layouts, styles, and placeholder icons are excluded.
- App-specific or domain behavior is not implemented in the host.
- Global CSS is limited to document reset, loading, and fatal boot-error states;
  the rendered component uses scoped CSS.
- Product PWA icons and release metadata remain owned by `P2-PWA-001`.

## Task List

- [x] `P2-HOST-001` Scaffold the standalone .NET 10 Blazor WASM PWA and add it to
  the solution without template sample pages or assets.
- [x] `P2-HOST-002` Reference Platform, Browser Infrastructure, and selected app
  projects.
- [x] `P2-HOST-003` Implement the composition root.
- [x] `P2-HOST-004` Validate the DI graph.
- [x] `P2-HOST-005` Implement host boot states.
- [x] `P2-HOST-006` Add the host error boundary and reporting.
- [x] `P2-HOST-007` Implement deterministic boot sequence and failure rollback in `EcosystemBootCoordinator.cs`.
- [x] `P2-HOST-008` Add independent recovery UI and recovery actions in `App.razor`.
- [x] `P2-HOST-009` Enforce shell-global versus scoped CSS ownership and `:root` design tokens in `app.css`.
- [x] `P2-HOST-010` Validate Debug, Release, trimming, and published static smoke tests with 0 warnings and 0 errors.
