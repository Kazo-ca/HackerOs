# Wave 2: OS Fundamentals Migration Report

## Overview

This report documents the migration of legacy OS Fundamentals applications and core infrastructure components from TypeScript (`src/`) into Blazor WebAssembly C# (`wasm2/HackerOs/`).

---

## Migrated Component Matrix

| ID | Feature Name | Legacy Source | C# Project / Location | Automated Test Evidence | Status |
|---|---|---|---|---|---|
| `P4-W2-001` | Settings App | `src/apps/settings.ts` | `Apps/System/HackerOs.Apps.Settings/` | `SettingsWindowTests` | **MIGRATED** |
| `P4-W2-002` | System Monitor | `src/apps/system-monitor.ts` | `Apps/System/HackerOs.Apps.SystemMonitor/` | `SystemMonitorWindowTests` | **MIGRATED** |
| `P4-W2-003` | Dialogs / Message Boxes | `src/core/dialog.ts` | `Shared/HackerOs.AppSdk.Blazor/` | `FileDialogServiceTests` | **MIGRATED** |
| `P4-W2-004` | Notifications | `src/core/components/notification.ts` | `Platform/HackerOs.Platform.Core/Notifications/` | `NotificationQueueTests` | **MIGRATED** |
| `P4-W2-005` | Error Log Viewer | `src/apps/error-log-viewer.ts` | `Apps/System/HackerOs.Apps.ErrorLogViewer/` | `ErrorLogViewerWindowTests` | **MIGRATED** |
| `P4-W2-006` | Local Authentication & Profiles | `src/core/user.ts` (ADR 0013) | `Platform/HackerOs.Platform.Core/Sessions/` | `LocalSessionServiceTests` | **MIGRATED** |
| `P4-W2-007` | Theme Tokens & Settings | `src/core/theme*.ts`, `src/core/themes/` | `HackerOs.Theming.Abstractions`, `HackerOs.Theming.Blazor`, appearance schema v2, `SettingsWindow.razor` | catalog/codec/Settings tests | **MIGRATED + EXPANDED** (9 bounded themes; no legacy arbitrary CSS) |
| `P4-W2-008` | Wave 2 Verification | Entire solution | `dotnet test HackerOs.sln` | Solution unit test suite | **MIGRATED** |
