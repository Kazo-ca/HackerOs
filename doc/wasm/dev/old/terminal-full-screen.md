# Full-Screen Terminal Contract and Nano

## Purpose

The optional full-screen terminal contract lets interactive commands own a character-cell alternate screen without referencing Blazor, DOM events, or a particular renderer. Existing stream-based commands remain source-compatible because `TerminalExecutionContext.FullScreen` is optional.

## Public contract

- `IFullScreenTerminalSession` enters and leaves the alternate screen, renders immutable `TerminalScreenFrame` values, exposes a `TerminalViewport`, and supplies cancellable `TerminalKeyEvent` values.
- `TerminalScreenFrame` contains text rows, a cursor, and an accessible label.
- Key and cursor types live in `HackerOs.AppSdk`; app commands do not depend on the browser.
- Implementations must release alternate-screen ownership even when command execution, input, or rendering is cancelled.

## Nano behavior

Nano reads through the app-scoped VFS gateway, enforces a 1 MiB UTF-8 limit, maintains a bounded cursor and line buffer, handles insertion/deletion/navigation, and writes through optimistic VFS revisions. New files are created using the observed parent revision. `Ctrl+O` saves, `Ctrl+Shift+O` prompts for Save As, and `Ctrl+X` applies dirty-buffer confirmation. Cleanup uses a non-cancelled leave operation in `finally`.

## Evidence and remaining work

- [x] Ordinary contexts without a full-screen service fail explicitly rather than pretending to edit.
- [x] Focused edit/save round trip passes against deterministic VFS and terminal fakes.
- [x] Cancellation after alternate-screen entry proves cleanup occurs.
- [x] Wire the per-window Blazor Terminal adapter into lifecycle/intent dispatch, key rendering, viewport resize reporting, and cancellation.
- [x] Verify exact browser key/frame behavior and regular-screen restoration in Chromium.
- [x] Verify Save As, denied capability, dirty confirmation, cancellation, and resize through focused deterministic tests.

`P4-W3-006` was rechecked on 2026-08-03 after the complete Release solution passed
618 tests. Focused evidence is `NanoCommandTests` (5),
`TerminalFullScreenSessionTests`, the full-screen cases in
`AppIntentDispatcherTests`, and
`IndexedDbBrowserContractTests.Terminal_full_screen_adapter_edits_and_restores_the_regular_screen`.
The representative axe scan also includes the full-screen Terminal renderer.

Optional-app packaging and offline delivery remain owned by the separate
build-known lazy-loading/PWA gates; they are not claimed by this application
behavior evidence.
