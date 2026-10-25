> [Version française](readme-fr.md)

# HackerOS

A simulation of a Linux-like operating system in the browser, in C# and Blazor
WebAssembly, meant as an environment to develop applications in. **No game in
this repository** (ADR 0135). It is the port of the JavaScript simulator in
`src/`, which is kept read-only as a behavioral reference and is not a
dependency of this solution.

## Quick start

```bash
dotnet build HackerOs.sln                      # .NET 10 (global.json)
dotnet test HackerOs.sln
dotnet run --project OS/HackerOs.Ecosystem     # the OS, as a standalone PWA
```

Prerequisites, debug hosts, publishing and container workarounds:
[`doc/en/wasm/dev/building-and-testing.md`](../../doc/en/wasm/dev/building-and-testing.md).

## Layout

| Folder | Contents |
|---|---|
| `Shared/` | Contracts (`*.Abstractions`) and the public app SDK (`HackerOs.AppSdk*`). |
| `Platform/` | OS services (`Platform.Core`) and its UI (`Platform.Blazor`, windows, taskbar, mobile, theme). |
| `Infrastructure/` | IndexedDB persistence and browser interop. |
| `Apps/` | Base apps (`System/`), shell commands (`Commands/`), samples (`Samples/`). |
| `OS/HackerOs.Ecosystem` | Shipped host: standalone, offline PWA. |
| `test/test`, `Server/` | Debug host; optional server (sync, accounts, network relay). |
| `Tools/`, `build/` | Manifest generator and MSBuild targets of the app model. |
| `Tests/` | Unit, MSBuild integration and E2E tests. |

Each project is described in
[`doc/en/wasm/dev/project-overview.md`](../../doc/en/wasm/dev/project-overview.md).

## Documentation

[`doc/en/wasm/README.md`](../../doc/en/wasm/README.md) indexes all the
documentation (written in French in [`doc/fr/wasm/`](../../doc/fr/wasm/README.md)
and translated to English). Where to start:

- [`project-overview.md`](../../doc/en/wasm/dev/project-overview.md): understand the code;
- [`sdk-developer-guide.md`](../../doc/en/wasm/framework/sdk-developer-guide.md) and
  [`sdk-reference.md`](../../doc/en/wasm/framework/sdk-reference.md): write an app;
- [`ecosystem-rebuild-plan.md`](../../doc/en/wasm/dev/plans/ecosystem-rebuild-plan.md): the
  project's direction and its decisions;
- [`doc/en/wasm/dev/adr/`](../../doc/en/wasm/dev/adr/README.md): architecture decisions;
- [`pitfalls.md`](../../doc/en/wasm/dev/pitfalls.md): pitfalls met and how they were solved;
- [`doc/en/wasm/memory/`](../../doc/en/wasm/memory/README.md): project memory (series state, conventions).

Contribution rules (where code goes, collocated CSS and JavaScript, docs kept
current with every change) are in [`AGENTS.md`](../../AGENTS.md) at the
repository root.
