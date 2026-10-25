# System Monitor & Process Management Architecture

## Purpose
System Monitor (`SystemMonitorWindow`) provides real-time monitoring of running processes, memory usage, CPU metrics, and process termination capabilities across HackerOS.

## Architecture & Event Flow
1. **Dynamic Process Querying**:
   - `SystemMonitorWindow` queries `IProcessManager.GetActiveProcesses()` to retrieve live running processes (kernel, desktop shell, terminal, calculator, file explorer, etc.).

2. **Event-Driven Auto Refresh**:
   - `SystemMonitorWindow` subscribes to `ProcessStateChangedEvent` from `IEventBus`.
   - Whenever any process starts, stops, faults, or transitions lifecycle state, `SystemMonitorWindow` automatically updates its process table without requiring manual refresh button clicks.

3. **Process Termination & Desktop Window Synchronization**:
   - Ending a process in System Monitor invokes `AppLifecycleOrchestrator.StopAsync(pid, ProcessExitReason.Killed)` (or `IProcessManager.Kill(pid)`).
   - `DesktopShell` listens to `ProcessStateChangedEvent`. When a process reaches a terminal state, `DesktopShell` executes `WindowRuntime.Apply(new ForceWindowCloseCommand(windowId))` to cleanly remove the corresponding desktop window frame and update the Taskbar.

## Task List
- [x] Design reactive process monitoring with `IEventBus` and `ProcessStateChangedEvent`.
- [x] Update `DesktopShell.razor` to automatically close window frames when process terminates.
- [x] Refactor `SystemMonitorWindow.razor` to query `IProcessManager`, subscribe to process lifecycle events, and implement process termination.
- [x] Add unit tests for `SystemMonitorWindow` process management.
- [x] Verify all unit and E2E tests pass.
