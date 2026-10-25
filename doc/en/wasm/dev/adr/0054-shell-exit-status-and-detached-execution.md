> [Version française](../../../../fr/wasm/dev/adr/0054-shell-exit-status-and-detached-execution.md)

# ADR 0054: `$?` per pipeline, detached shell and synchronous writes to a pipe

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 3, Shell, commands and trash
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.1).
- **Partially replaces:** ADR 0050, section 3, for a single point: the value
  of `$?`. The rest of ADR 0050 remains in force.
- **Clarifies:** ADR 0050, sections 4 and 7, on three points the ADR did not
  settle.

## Context

While implementing the interpreter (work item `rebuild/3-shell-interpreter`), four
points turned out to be missing or contrary to bash behavior:

1. ADR 0050 defines `$?` as the code of "the last **list** executed". In
   bash, `$?` follows each **pipeline**: in `false && ...` then
   `echo $?`, the result is the same, but in `true && echo $?` bash prints
   the code of `true`, not that of the previous line.
2. The ADR wants the steps to run at the same time and a writer to wait when
   the pipe is full, but WebAssembly has only one thread: a **synchronous**
   write (`Write`, `WriteLine`) cannot wait.
3. The ADR wants `ExecuteCommandIntent` to use the calling process as the
   shell's process, but the intent request only carries the calling app's
   identifier, not its context.
4. `ExecuteCommandIntent` can carry a single-use elevation token while the line
   can launch several commands.

## Decision

1. **`$?` is updated after each pipeline**, as in bash. The code of a line
   remains that of the last pipeline executed. An empty line does not
   change `$?`.
2. **A synchronous write to a full pipe does not wait**: the buffer
   temporarily exceeds its capacity. Only asynchronous writes
   wait for space. Synchronous reads keep the rule of
   ADR 0050 (exception on an empty pipe that is still open).
3. **`AppIntentDispatcher.DispatchAsync` accepts an optional caller context**
   (`callerContext`). When provided, its process is the shell's process
   (parent of the commands, starting directory) and its file gateway
   opens the redirections. When absent, the line runs in a **detached shell**:
   commands with no parent (login environment), directory
   `ExecuteCommandIntent.WorkingDirectory` or home folder, and any
   redirection is refused (`hsh: <target>: cannot open files without a calling
   app`, code `1`).
4. **The elevation token of an `ExecuteCommandIntent` is consumed by the
   first command launched** on the line; the following ones run without
   elevation.

## Consequences

- `a && echo $?` prints `0`, as in bash.
- A command that synchronously writes a lot of data to a pipe consumes
  memory instead of blocking; all built-in commands write
  asynchronously.
- An app that wants redirections in `ExecuteCommandIntent` passes its
  context. The terminal no longer uses the intent: it calls
  `ShellInterpreter` directly.

## References

- ADR 0042 (elevation), ADR 0050 (shell grammar), ADR 0051 (commands).
- [`shell-interpreter.md`](../shell-interpreter.md).
