# Rendering a window in a Web Worker (trial)

> **Status: trial finished, verdict "we stop here"** ([ADR 0133](adr/0133-window-rendering-in-worker-verdict.md)).
> The code stays in the repository, off by default. No app must
> depend on it; windows run on the main thread (ADR 0070 §1) and a
> heavy load goes through `HackBackground` ([`background-jobs.md`](../framework/background-jobs.md)).

## Role

Show that a Blazor window app can run in a Web Worker, without a
DOM, while drawing itself in its window on the page, and measure what this
costs (ADR 0129). The subject is the Calculator, unchanged.

## Enabling the trial

In `OS/HackerOs.Ecosystem/wwwroot/appsettings.json`:

```json
"HackerOs": {
  "RemoteWindowRendering": {
    "Enabled": true,
    "Apps": [ "org.hackeros.calculator" ]
  }
}
```

- `Enabled` (default `false`): without it, no service is registered and
  nothing changes.
- `Apps`: identifiers of the window apps rendered in a worker. An app
  with its own JavaScript interop will not work (no `IJSRuntime`
  in the worker).
- Only in the browser, with workers (`AddHackerOsWorkerProcesses`).
  If the remote process host is not the workers one (tests, Server
  in interactive mode), the window renders on the page as before.

## Architecture

```
Page (fil principal)                               Worker dédié
────────────────────                               ────────────
WindowAppRenderer                                  WorkerProcessHost
  └─ IRemoteWindowRendering ? ── oui ──┐             └─ start k=3 → WorkerWindowRunner
       RemoteWindowHost (razor)        │                  ├─ WorkerWindowRenderer : Renderer
         ├─ StartWindowAsync ──────────┼── start ──►      │    └─ WorkerWindowRoot
         │                             │                  │         └─ CascadingValue(AppContext)
         │                             │                  │              └─ CalculatorWindow
         ├─ RemoteWindowHost.razor.js ◄┼── ui {k:"b"} ─── │  DomPatchWriter (RenderBatch → ops)
         │    (exécute les ops)        │                  │
         └─ DispatchEvent ─────────────┼── uin {k:"e"} ─► │  DomEventArgsReader → DispatchEventAsync
              dialogues de l'OS ◄──────┼── ui {k:"dlg"}   └─ RemoteDialogService
                                 ──────┼── uin {k:"dlgr"} ►
```

| Project | Types |
|---|---|
| `Platform/HackerOs.Platform.Blazor` | `IRemoteWindowRendering` (`GetHostComponent`); `WindowAppRenderer` consults it if it is registered. |
| `Platform/HackerOs.Platform.WorkerHost` | `ui` (worker → OS) and `uin` (OS → worker) messages; `start` with `k = 3`, `ra`/`rt` (the runner); on the worker side `IWorkerWindowRunner`, `WorkerWindowRun`; on the OS side `WorkerRemoteProcessHost.StartWindowAsync`, `RemoteWindowProcess`, `RemoteProcessSession.UiReceiver`. |
| `Platform/HackerOs.Platform.RemoteRendering` (new) | **Worker/**: `WorkerWindowRunner`, `WorkerWindowRenderer`, `WorkerWindowRoot`, `DomPatchWriter`, `DomEventArgsReader`, `RemoteDialogService`. **Host/**: `RemoteWindowHost` (+ `.razor.cs`, `.razor.css`, `.razor.js`), `RemoteWindowRendering`, `RemoteWindowRenderingOptions`. `AddHackerOsRemoteWindowRendering`. |
| `OS/HackerOs.Ecosystem.Host` | Reads the `HackerOs:RemoteWindowRendering` section and calls `AddHackerOsRemoteWindowRendering`. |

### Process

- The window takes the ready worker as a `dedicated` process (ADR 0071);
  it has its pid, its remote context and its gateways (ADR 0070, 0124).
- The chrome (title, borders, buttons, shortcuts) is still drawn by the page:
  a window that freezes in its worker can still be moved and closed.
- Closing the window sends `{k:"close"}`; after 2 s without an end, the worker is
  terminated. An abnormal end displays an alert in the window.

### DOM operations

The worker keeps a mirror of Blazor's logical tree (components and
regions have no DOM node) and writes, for each `RenderBatch`, a `ui` message
`{k:"b", n, o:[...], us, nodes}`. Node 0 is the window's content
element; `before = 0` means "at the end".

| Code | Form | Role |
|---|---|---|
| 1 | `[1, id, parent, before, tag, svg]` | Create an element (SVG if `svg = 1`). |
| 2 | `[2, id, parent, before, texte]` | Create a text node. |
| 3 | `[3, id, parent, before, html]` | Insert markup (one or more nodes). |
| 4 | `[4, id]` | Remove a node and its subtree. |
| 5 | `[5, id, nom, valeur]` | Set an attribute; `value` and `checked` are set as properties. |
| 6 | `[6, id, nom]` | Remove an attribute. |
| 7 | `[7, id, texte]` | Change a text. |
| 8 | `[8, id, événement, gestionnaire]` | Listen to an event (`0` = stop). |
| 9 | `[9, id, parent, before]` | Move (lists with `@key`). |
| 10 | `[10, id, 'p'\|'s', événement, 0\|1]` | `preventDefault` / `stopPropagation`. |

A true boolean attribute becomes an empty attribute; false or `null` removes it.

### Events

The page calls `DispatchEvent(handler, name, json)` on the host, which
sends `uin` `{k:"e", h, n, a}`. The worker rebuilds `MouseEventArgs`,
`PointerEventArgs`, `WheelEventArgs`, `KeyboardEventArgs`, `ChangeEventArgs`
or `FocusEventArgs`, and for `input`/`change` an `EventFieldInfo` (the field
keeps what the user types). An event for a handler that has already been
removed is noted in the log and ignored.

### SDK dialogs

| Dialog | In a worker |
|---|---|
| `MessageBoxAsync`, `TextInputAsync` | Relayed: `{k:"dlg"}` → OS dialog on the page → `{k:"dlgr"}`. |
| `CredentialPromptAsync` | Answers "cancelled": no password enters a worker. |
| File dialogs | `NotSupportedException`. |

## Measurements

See [ADR 0133](adr/0133-window-rendering-in-worker-verdict.md): opening
+≈ 340 ms, p95 latency +3.3 ms, **66.5 MiB** per window worker (55.4 MiB
for the runtime and the boot assembly set), batches of 72 bytes on average.

To redo them:

```bash
build/publish-trimmed.sh   # ou dotnet publish OS/HackerOs.Ecosystem -c Release -o artifacts/publish-trimmed/publish
python3 -m http.server 8601 --bind 127.0.0.1 -d artifacts/publish-trimmed/publish/wwwroot &
HACKEROS_TRIMMED_URL=http://127.0.0.1:8601 HACKEROS_TRIMMED_ARTIFACTS=$PWD/artifacts/publish-trimmed/remote \
  dotnet test Tests/HackerOs.TrimmedPublish.E2E.Tests -c Release --filter "FullyQualifiedName~calculator"
```

The test enables the trial by rewriting `appsettings.json` on the fly and writes
`remote-window-measurements.json`.

## Key decisions

- **Translate in the worker** rather than replaying the `RenderBatch` on the page side:
  Blazor's binary format and its applier are internal (ADR 0129 §2).
- **`BL0006` disabled** in the files that read the `RenderTree`
  types: public but announced as subject to change; the unit tests
  are the safeguard for a .NET upgrade.
- **`RemoteWindowHost` is public**: Razor generates public components; it
  is in the platform's `PublicAPI.Unshipped.txt`, not the SDK's.
- **The runner is not checked by `KnownAssemblies`**: it is named by the
  OS, not by the app; only the app's assembly is.

## Limits

- No programmatic focus, no JavaScript interop from the app, no
  detached window (ADR 0078), no file dialogs.
- One worker per window: ≈ 66 MiB each.

## Tests

| Project | What it verifies |
|---|---|
| `Tests/HackerOs.Platform.RemoteRendering.Tests` | `WorkerWindowRenderer` + `DomPatchWriter` render real components in a fake DOM (`FakeDom`, same logic as the JavaScript module): first render, texts, conditional elements, boolean attributes, flattened components and regions, `@key` list moved without recreation, `@bind` (text, checkbox), event flags, SVG, removed handler, handler that throws; `DomEventArgsReader`; `RemoteDialogService`. |
| `Tests/HackerOs.TrimmedPublish.E2E.Tests` (`TrimmedPublishTests.RemoteWindows.cs`) | On the trimmed publication: the Calculator in a worker computes with the mouse and keyboard, keeps its CSS, `Tab`, axe, `Alt+F7`/`Alt+Q`, dedicated worker released; and the measurements. |

## Task list of work item `exec/6-remote-renderer-poc`

- [x] `ui`/`uin` messages and window startup (`start` `k = 3`) in `WorkerHost`.
- [x] Worker `Renderer`, cascading root, DOM operation translator.
- [x] Page window host and co-located JavaScript executor.
- [x] Events (mouse, pointer, wheel, keyboard, input, focus) and `@bind`.
- [x] SDK dialogs relayed (message, input); credentials and files refused.
- [x] Setting `HackerOs:RemoteWindowRendering`, off by default.
- [x] E2E test on the trimmed publication and automated measurements.
- [x] Unit tests of the translator, events and dialogs.
- [x] ADR 0133: verdict "we stop here"; decision card to Benoit.
- [x] Docs: this file, `worker-processes.md`, index.
