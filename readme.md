# Hacker Simulator

A simulator game that recreates the experience of being a hacker, featuring multiple applications including a terminal, code editor, and system monitoring tools.

Source repository: [Kazo-ca/HackerOs](https://github.com/Kazo-ca/HackerOs).

Run [HackerOS WebAssembly](https://hackos.kazo.ca/) or the
[original TypeScript version](https://hackos.kazo.ca/legacy/).
Both are published by one [GitHub Pages workflow](.github/workflows/deploy-to-pages.yml),
with independent cached builds; see the [deployment guide](doc/wasm/dev/github-pages-deployment.md).

## Active codebase: HackerOS (C# / Blazor WebAssembly)

The project is being ported to C# and Blazor WebAssembly under
[`wasm2/HackerOs/`](wasm2/HackerOs/README.md). That is where all new work
happens; the TypeScript application described below (`src/`) is kept read-only
as a behavioral reference. Documentation (in French) starts at
[`doc/wasm/README.md`](doc/wasm/README.md), with a
[project overview](doc/wasm/dev/project-overview.md) and an
[app SDK developer guide](doc/wasm/framework/sdk-developer-guide.md).

## Project Overview

Hacker Simulator is an interactive web-based application that simulates various hacking scenarios and computer environments. Users can interact with multiple applications including a terminal, text editor, code editor, system monitor, file explorer, and more.

## Getting Started

### Prerequisites

- Node.js 20 (the legacy publishing workflow uses this version)
- npm (v6 or higher)

### Installation

1. Clone the repository
2. Install dependencies:
   ```
   npm install
   ```

### Development

```bash
npm run dev         # Start the dev server with hot reloading
npm run tsc:debug   # Compile all TS files individually with source maps
```

### Build

```bash
npm run build:prod  # Creates app.js and app.min.js in the dist folder
```

## Project Structure

- `/src`: Main source code
  - `/apps`: Individual applications (terminal, code editor, etc.)
  - `/commands`: Command processing for terminal
  - `/core`: Core functionality and application management
  - `/missions`: Game missions and scenarios
  - `/server`: Server-side code
  - `/websites`: Web content for in-game browsing

## Features

- Multiple simulated applications:
  - Terminal with command-line interface
  - Code editor with syntax highlighting
  - Text editor for managing documents
  - File explorer for navigating the virtual file system
  - System monitor to track resources
  - Browser for in-game web content
  - Calculator for basic calculations

## Contributing

Please read our [contribution guidelines](CONTRIBUTING.md) before submitting pull requests.

## License

This project is licensed under the terms found in [LICENSE.md](LICENSE.md).


## Blazor WebAssembly Version

A new Blazor WebAssembly implementation targeting .NET 9 is located in [`src/wasm`](src/wasm). This version aims to recreate the JavaScript functionality of the original project purely in C#.
Core services such as a simple kernel, shell, and in-memory file system are implemented to manage processes and applications without any JavaScript.
