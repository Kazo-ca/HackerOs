> [Version française](../../../../fr/wasm/dev/adr/0014-shell-grammar-boundary.md)

# ADR 0014: First-Slice Shell Grammar Boundary

## Status

Accepted on 2026-08-01.

## Context

The first terminal slice needs predictable command invocation, quotes,
environment expansion, working directory, history, cancellation, and exit status.
It does not need a complete POSIX shell. The legacy parser mixes shell tokenizing
with command-specific option parsing and renderer/global-OS access; v3 commands
must instead receive renderer-independent streams and raw positional tokens.

## Decision

### Input unit

One submitted line represents zero or one command invocation. Leading/trailing
Unicode whitespace is ignored. An empty or whitespace-only line succeeds without
creating a command process or history entry.

The parser returns:

- original input;
- command name;
- ordered argument tokens;
- source spans for diagnostics; and
- a structured syntax error instead of throwing for user input.

The shell resolves the command name and manifest-declared static aliases. It does
not parse flags or `--name=value`; each command receives raw tokens and owns its
documented options.

### Tokenization and quoting

Unquoted Unicode whitespace separates tokens. Adjacent quoted/unquoted fragments
form one token.

- Single quotes preserve every character literally until the next `'`.
- Double quotes preserve whitespace and allow environment expansion plus these
  escapes: `\"`, `\\`, `\$`, `\n`, `\r`, and `\t`.
- Outside quotes, backslash escapes the next character.
- Empty quotes produce an empty token.
- Unterminated quotes, trailing escape, invalid variable syntax, and unsupported
  operators produce structured errors containing code, offset, and length.

The parser does not perform globbing, tilde expansion, brace expansion, word
splitting after variable expansion, or filename generation in the first slice.
Arguments remain exactly one token per parsed token.

### Environment expansion

`$NAME` and `${NAME}` expand outside single quotes. Names match
`[A-Za-z_][A-Za-z0-9_]*`. Missing variables expand to an empty string; strict
undefined-variable mode is deferred. Expansion never recursively reparses the
result as shell syntax.

The initial read-only environment includes `USER`, `HOME`, `PWD`, `SHELL`,
`TERM`, and approved app/profile values. Commands receive an immutable snapshot.
Persistent `export`, `unset`, and shell startup files are deferred.

### Explicitly unsupported syntax

Outside quotes, these operator characters are rejected with an unsupported
syntax error rather than passed ambiguously to commands:

```text
| & ; < > ` ( )
```

Therefore the first slice excludes:

- pipelines and redirection;
- background jobs and job control;
- command lists/conditionals;
- command/process substitution;
- subshells and functions;
- shell scripts and source files;
- variable assignment syntax; and
- wildcard/glob expansion.

Literal operator characters must be quoted or escaped.

### Session state

Each terminal window owns an independent shell session containing authenticated
user/session identity, current `VirtualPath`, environment, history, last exit
status, command correlation, and active command cancellation.

The initial working directory is the user's home. `cd` returns a structured
directory-change result; the shell validates and applies it after successful
command completion. Commands never mutate a process-global current directory.

History stores accepted non-empty input lines in session order. It is volatile in
the first slice. Duplicate suppression and persistent history settings are
deferred.

### Execution and exit status

The first token resolves through enabled terminal app manifests and static
aliases. Duplicate command/alias registrations reject the catalog before shell
execution. Command not found writes a stable message to stderr and returns exit
status `127`. Syntax errors do not launch a command and return status `2`.

Commands execute through `TerminalAppBase` with separate `TextReader`, stdout,
and stderr streams, immutable arguments/environment/cwd, app-scoped gateways, and
a linked cancellation token. Zero means success. Nonzero command statuses are
preserved. Cancellation returns status `130` unless the command completed before
cancellation commit. Unhandled faults are logged with correlation ID, produce a
safe stderr message, and return status `1`.

The shell records last exit status after every submitted non-empty line. `$?` is
not expansion syntax in the first slice; the renderer may display status and a
later grammar version may add special parameters.

### Renderer boundary

The shell/parser is headless. It has no xterm.js, Blazor, DOM, or terminal-window
dependency. The terminal emulator owns line editing, key handling, display,
history navigation, completion UI, ANSI rendering, and resize. It translates
renderer input/output to the shell's text streams.

## Consequences

- Parsing is deterministic and independently testable.
- Commands own flags, avoiding a shell-wide option convention.
- Quoted operator characters remain usable while unsupported shell features fail
  clearly.
- Independent terminal windows cannot corrupt global cwd/environment state.
- Advanced POSIX-like features can be added by a later grammar/version decision.

## References

- `docs/apps/terminal.md`
- `docs/app-contracts.md`
- `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md` sections 7.6 and 15
- `src/commands/command-processor.ts` (behavioral reference only)