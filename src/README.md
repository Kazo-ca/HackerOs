> [Version française](readme-fr.md)

# HackerSimulator legacy (TypeScript)

This folder holds the **first version** of HackerSimulator, written in
TypeScript and LESS and bundled by Webpack. It is kept **read-only** as a
behavioral reference: all active work happens in the WebAssembly version,
[`wasm2/HackerOs/`](../wasm2/HackerOs/README.md), documented in
[`doc/en/wasm/`](../doc/en/wasm/README.md).

The legacy version is published at <https://hackos.kazo.ca/legacy/> by the same
GitHub Pages workflow as the WebAssembly version
([`github-pages-deployment.md`](../doc/en/wasm/dev/github-pages-deployment.md)).

## Build and run

Prerequisites: Node.js 20 and npm. Run the commands from the repository root.

```bash
npm install
npm run dev          # development server with hot reload
npm run build:debug  # development bundle in dist/
npm run build:prod   # minified bundle in dist/
npm run tsc:debug    # compiles each TypeScript file with its source maps
```

## Layout

| Folder | Contents |
| --- | --- |
| [`index.ts`](index.ts), [`index.html`](index.html) | Entry point and host page. |
| [`core/`](core/) | Core: file system, processes, windows, desktop, Start menu, themes, settings, simulated network, multiple monitors. |
| [`apps/`](apps/) | Applications: terminal, code editor, text editor, file explorer, browser, calculator, system monitor, Hack Paint, settings, theme editor, error log viewer. |
| [`commands/`](commands/) | Terminal command processor and Linux commands (`ls`, `cat`, `grep`, `ps`, `curl`, `nmap`, `ping`…). |
| [`websites/`](websites/) | Simulated web sites served to the built-in browser. |
| [`styles/`](styles/) | Shared LESS style sheets. |
| [`docs/`](docs/README.md) | Documentation of the legacy version's features. |

## Mapping to the WebAssembly version

The apps and commands of this folder were ported to C# in
`wasm2/HackerOs/Apps/`. The archived migration reports are in
[`doc/en/wasm/dev/old/migration/`](../doc/en/wasm/dev/old/migration/README.md).
