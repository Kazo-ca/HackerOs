# Plan for compiling and running user code (hybrid WASM/server)

**Status:** technical proposal, not implemented, no `D-xxx` decision
recorded, no code written.

## 1. Goal

Make it possible to design an application for the HackerOS ecosystem in C#,
compile it and run it directly in the browser (Blazor WebAssembly),
while keeping an equivalent server execution path, **without duplicating the
compilation logic** between the two environments.

This document captures the feasibility exploration carried out in a session (2026-08-17)
before any implementation commitment. It serves as a reference point if this
work item is ever prioritized.

## 2. Context and related documents

- [ADR 0020](../adr/0020-editor-framework-and-script-sandbox.md) strictly separates
  code editing (`vfs.read`/`vfs.write`, no execution) from
  running a user script/program, controlled by the `user-scripts.execute`
  capability and intended to run in an isolated sandbox. This plan
  is the natural follow-up to that decision: it does not challenge it, it
  proposes how `user-scripts.execute` could be honored for compiled C#
  rather than for a simple script.
- [`code-editor.md`](code-editor.md) documents the current editor, which has and
  must keep no compilation or execution capability until this plan
  (or an ADR derived from it) is accepted.
- [ADR 0023](../adr/0023-optional-game-domain-and-proxy-fallback.md) already establishes
  the architectural pattern reused here: a shared interface in an
  `*.Abstractions` project, with several implementations (local, server proxy,
  fallback) selected by DI/capability manifest; see `IGameDomainGateway`.
  This plan applies the same pattern to code compilation/execution.

## 3. Technical feasibility (summary of the exploration)

Compiling and running arbitrary C# from inside a Blazor WebAssembly
host is achievable, with specific constraints:

- **In-memory compilation** via Roslyn (`Microsoft.CodeAnalysis.CSharp`,
  `CSharpCompilation`) then `Assembly.Load(byte[])`: the resulting assembly is
  run by the Mono IL interpreter, with no need for a native JIT. This is the
  approach already used by well-known external projects (e.g. Blazor REPL).
- **No `Reflection.Emit`**: no on-the-fly dynamic IL generation
  is possible in WASM. Only full source compilation through Roslyn
  works.
- **AOT mode**: an assembly loaded dynamically at runtime cannot
  itself be AOT-compiled; it falls back to the interpreter (mixed mode
  supported since .NET 7+). Verified in this repo: no `.csproj` enables
  `RunAOTCompilation` today, so this path is not blocked as things stand.
- **Trimming**: Roslyn compilation needs the reference assemblies and
  reflection metadata intact. `PublishTrimmed` is already `false` on
  `HackerOs.Ecosystem` and `HackerOs.Server` today; a future switch to
  `true` on these projects would break this path without complete
  `[DynamicDependency]` annotations.
- **Isolation**: WebAssembly provides **no** isolation against the host's own C#
  code. A dynamically loaded assembly runs in the same
  process, with the same JS-interop access as the rest of the application. The
  isolation guarantee required by ADR 0020 ("isolated web-worker sandbox
  without access to host DOM or sensitive OS APIs") must therefore be built
  architecturally (for example a dedicated Web Worker with no JS-interop
  binding to the host DOM/API, communication limited to a typed
  `postMessage` channel) and not assumed to be provided by the runtime.

## 4. Proposed hybrid architecture

Modeled on `IGameDomainGateway` (ADR 0023):

- A new shared project, e.g. `Shared/HackerOs.CodeExecution.Abstractions`,
  would define two distinct contracts:
  - **`ICompilationService`**: C# source → in-memory assembly + diagnostics.
    Can be **truly shared at 100%** between client and server: Roslyn
    is portable C#, a single implementation is enough for both sides.
  - **`IExecutionSandbox`**: running a compiled assembly. The **contract**
    is shared, but **not the implementation**:
    - WASM client side: local execution through the Mono interpreter, isolation
      built through a dedicated Worker (see section 3).
    - Server side: OS-level isolated execution (container or separate
      process; modern .NET no longer has AppDomains), exposed by
      `HackerOs.Server` and reached from the client through the proxy bridge already
      used for `curl`/`nmap` (same pattern family as the network proxy
      documented in `server-implementation-pass.md`).
- Selection of the implementation (local vs server) at execution time,
  depending on server availability and policy, with the same fallback logic as
  `IGameDomainGateway`/`NullGameDomainGateway`.
- Manifest capability gate: `user-scripts.execute` (already defined by ADR
  0020), whether for the local path or the server path.

## 5. Out of scope for now

- Any code implementation (this document is deliberately not coded).
- The final choice of the server isolation mechanism (container vs native
  process sandbox), which needs its own design/ADR when the time comes.
- The resource limits (CPU, memory, execution time) imposed on the executed
  user code.
- The UI/UX of a possible "design and run an app for the
  ecosystem" app; this document covers only the underlying
  compilation/execution layer.

## 6. Possible next steps (not committed)

- [ ] Formal `D-xxx` decision if this work item is ever prioritized.
- [ ] Dedicated ADR once the architecture is validated by the user.
- [ ] Isolated compile-only prototype (no execution) to confirm
  Roslyn in WASM interpreter mode in this specific repo, before any work on the
  execution sandbox.

## 7. Reference documents

- [ADR 0020: Code editor framework and script sandbox policy](../adr/0020-editor-framework-and-script-sandbox.md)
- [ADR 0023: Optional Game Domain integration and network proxy fallback](../adr/0023-optional-game-domain-and-proxy-fallback.md)
- [`code-editor.md`](code-editor.md)
- [`server-implementation-pass.md`](server-implementation-pass.md)
- [`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md)
