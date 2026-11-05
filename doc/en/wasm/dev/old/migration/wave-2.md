> [Version française](../../../../../fr/wasm/dev/old/migration/wave-2.md)

# Wave 2: OS Fundamentals Migration Report

## Overview

This report documents the migration of legacy OS Fundamentals applications and core infrastructure components from TypeScript (`src/`) into Blazor WebAssembly C# (`wasm2/KazoOs/`).

---

## Migrated Component Matrix

| ID | Feature Name | Legacy Source | C# Project / Location | Automated Test Evidence | Status |
|---|---|---|---|---|---|
| `P4-W2-001` | Settings App | `src/apps/settings.ts` | `Apps/System/Kazo.Os.Apps.Settings/` | `SettingsWindowTests` | **MIGRATED** |
| `P4-W2-002` | System Monitor | `src/apps/system-monitor.ts` | `Apps/System/Kazo.Os.Apps.SystemMonitor/` | `SystemMonitorWindowTests` | **MIGRATED** |
| `P4-W2-003` | Dialogs / Message Boxes | `src/core/dialog.ts` | `Shared/Kazo.Os.AppSdk.Blazor/` | `FileDialogServiceTests` | **MIGRATED** |
| `P4-W2-004` | Notifications | `src/core/components/notification.ts` | `Platform/Kazo.Os.Platform.Core/Notifications/` | `NotificationQueueTests` | **MIGRATED** |
| `P4-W2-005` | Error Log Viewer | `src/apps/error-log-viewer.ts` | `Apps/System/Kazo.Os.Apps.ErrorLogViewer/` | `ErrorLogViewerWindowTests` | **MIGRATED** |
| `P4-W2-006` | Local Authentication & Profiles | `src/core/user.ts` (ADR 0013) | `Platform/Kazo.Os.Platform.Core/Sessions/` | `LocalSessionServiceTests` | **MIGRATED** |
| `P4-W2-007` | Theme Tokens & Settings | `src/core/theme*.ts`, `src/core/themes/` | `Kazo.Os.Theming.Abstractions`, `Kazo.Os.Theming.Blazor`, appearance schema v2, `SettingsWindow.razor` | catalog/codec/Settings tests | **MIGRATED + EXPANDED** (9 bounded themes; no legacy arbitrary CSS) |
| `P4-W2-008` | Wave 2 Verification | Entire solution | `dotnet test Kazo.Os.sln` | Solution unit test suite | **MIGRATED** |
