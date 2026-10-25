# Initial App Contracts

## Purpose

Define the first stable, browser-independent contracts used by every HackerOS
application. These contracts intentionally precede registry, storage, and UI
implementation so those systems depend on validated app metadata instead of
hard-coded app IDs.

## Architecture

### `HackerOs.App.Abstractions`

This project owns:

- `AppKind`: Window, Terminal, or Service;
- `AppAuthority`: ordered User, Administrator, and System authority;
- `AppCapabilities`: the exact capability catalog supported by this SDK version;
- `VirtualPath`: canonical absolute virtual paths with traversal protection;
- typed versioned core intents and authenticated intent envelopes;
- `AppManifest` and its entry-point, dependency, file-handler, terminal, and SDK
  compatibility records;
- `SemanticVersion`, including Semantic Version 2.0.0 prerelease precedence;
- `AppManifestValidator`; and
- structured manifest validation results.

It contains no runtime loading, reflection, Blazor, storage, or policy
implementation.

### `HackerOs.AppSdk`

This project owns:

- `AppBase`, which rejects invalid or mismatched manifests before app code runs;
- `IAppExecutionContext`, which exposes instance/user identity and granted
  capability names without exposing root DI;
- `TerminalAppBase` and `TerminalExecutionContext`, which use .NET text streams;
  and
- `ServiceAppBase`, which models active-session work and bounded stop hooks.

`WindowAppBase` is deliberately absent. It will live in a Blazor-specific SDK
project so terminal and service apps do not reference Blazor. That project now
exists as `HackerOs.AppSdk.Blazor`; see `blazor-app-sdk.md`.

## Manifest usage

Each app supplies a manifest with an immutable reverse-domain ID, semantic
package version, publisher, app kind, managed entry point, and App SDK range.
Kind-specific rules currently include:

- Terminal apps require command metadata.
- Only Terminal apps may declare command metadata.
- Only Window apps may declare file handlers.
- Only Window apps may declare capabilities that render modal UI, such as the
  file-open, file-save, and folder-select dialog capabilities.
- IDs, dependencies, capabilities, aliases, extensions, and actions are checked
  before app activation.

Validation returns all detected failures with stable error codes and property
paths. First-party build tooling and future package installation must treat an
invalid result as a hard failure.

The accepted canonical wire format is defined by ADR 0010. Each app will own one
strict `app.manifest.json` file using lower camel case, versioned JSON Schema,
source-generated `System.Text.Json`, rejected unknown/duplicate properties, and
deterministic compact serialization. Generated C# or assembly metadata is derived
output, never a second author-maintained source.

Fallback `name` and `description` remain in the canonical manifest. Localization
files may translate presentation/help resources but cannot alter identity,
capabilities, entry points, dependencies, lifecycle, or policy. Static assets use
package-relative paths and SHA-256 integrity values.

Package and SDK ranges use `SemanticVersion`, not `System.Version`, so prerelease
dependencies are correctly lower precedence than their corresponding release and
build metadata does not affect compatibility.

## Authority usage

`AppAuthorityPolicy.Satisfies(actual, required)` implements the ordered hierarchy
`System > Administrator > User`. This is an authorization primitive, not a role
grant mechanism.

An app manifest does not contain an authority field. Trusted build/install policy
will identify system apps, while the execution context carries the acting user's
authority. This prevents a system Text Editor from lending System authority to a
normal user editing protected settings.

Capability matching is exact, ordinal, and case-sensitive. Wildcard grants are
not supported. Destination, path, and similar restrictions belong in trusted
policy constraints so a broad string grant cannot accidentally expand access.

## Intent usage

Core intents use stable namespaced/versioned IDs and typed payload records for
app launch, file open/edit, file reveal, command execution, and settings display.
Files use canonical `VirtualPath` values. Apps dispatch intents through the
future platform intent service and never reference another app implementation.

## Terminal usage

A terminal app derives from `TerminalAppBase` and implements `ExecuteAsync`.
Input, output, and errors use `TextReader`/`TextWriter`, allowing command tests to
run without xterm.js or a browser. The terminal emulator will translate between
these streams and its renderer.

## Service usage

A service app derives from `ServiceAppBase` and implements `RunCoreAsync`. The OS
session token controls its lifetime. On shutdown, the ecosystem cancels the
session token and may call `StopAsync` with a bounded cleanup token. Apps cannot
assume cleanup runs after abrupt browser termination and have no resume-state
contract.

## Key decisions

- Public contracts target .NET 10.
- Manifests are validated without loading app assemblies.
- App authority is external policy, not self-declared metadata.
- App SDK contexts remain narrow and capability-oriented.
- UI dependencies remain outside the headless SDK.

## Task list

- [x] Define and validate initial manifest contracts.
- [x] Define ordered authority evaluation.
- [x] Define terminal execution streams and cancellation.
- [x] Define session service execution and stop reasons.
- [x] Test valid and invalid contracts.
- [ ] Add canonical JSON serialization and schema fixtures.
- [x] Add the initial exact capability catalog and operation context.
- [x] Add typed versioned intent and launch contracts.
- [x] Add canonical virtual paths.
- [x] Add settings/filesystem projection interfaces.
- [x] Add SemVer-compatible dependency range primitives.
- [x] Add Blazor window app contracts and file dialog requests/results.