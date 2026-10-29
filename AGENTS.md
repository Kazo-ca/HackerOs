# AGENT OPERATING GUIDELINES & DIRECTIVES

**Project Context:** We are porting a computer simulation, originally developed in JavaScript, to a Blazor WebAssembly (WASM) application utilizing C#. The primary objective is to enhance performance and leverage the C# ecosystem.

**Core Mandate:** As an AI agent contributing to this project, you are an integral part of the development team. Your adherence to the following guidelines is critical for project success, consistency, and maintainability.

---

**Before you start:** read [`doc/en/wasm/dev/pitfalls.md`](doc/en/wasm/dev/pitfalls.md) (pitfalls met during the whole rebuild and how they were solved) and [`doc/en/wasm/dev/old/common-pitfalls.md`](doc/en/wasm/dev/old/common-pitfalls.md) — recurring mistakes already made (and fixed) once in this codebase around static-asset routing, `AppManifest`/schema drift, IndexedDB versioning, E2E test harness setup, and ARIA states bound to `bool` in Razor markup. Each one was expensive to re-diagnose from scratch; don't repeat them.

---

## I. CRITICAL DIRECTIVES:

1.  **Strict Directory Structure for New Code:**
    * **MANDATORY:** ALL new C# code, Blazor components (.razor files), WASM interop logic, helper classes, services, or any other source code files you generate **MUST** be created and located **exclusively within the `wasm2\HackerOs\` directory** or its subdirectories.
    * **ABSOLUTELY PROHIBITED:** Under **NO circumstances** are you to create, modify, or place any new code files within the `src/` directory. `src/` is the legacy TypeScript implementation, kept **read-only** as a behavioral reference (existing feature rules, UI intent, data shapes) when porting something to `wasm2/HackerOs/`. It is not a dependency of the WASM solution.
    * **Three host projects currently serve `wasm2/HackerOs/`** (see [`doc/en/wasm/dev/old/hosting-model.md`](doc/en/wasm/dev/old/hosting-model.md) for the full picture):
        * `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj` — the standalone, self-contained Blazor WebAssembly PWA (own `wwwroot/index.html`). This is the production/offline-first host and must always work with no server present.
        * `test/test/test.csproj` — a Blazor Web App used only to debug the same WASM components interactively (`Components/App.razor`, `AddInteractiveWebAssemblyRenderMode`). Not a distribution target.
        * `Server/HackerOs.Server/HackerOs.Server.csproj` — the **optional** ASP.NET Core backend (sync, identity, HTTP/TCP/UDP proxy). It also serves the Ecosystem's published OS files as they are, so the browser runs the same WebAssembly OS as the standalone PWA (ADR 0130; Interactive Server rendering and direct service injection, ADR 0027/0036, are gone). The OS it serves connects to it by itself and shows the state in the taskbar's notification area (ADR 0134, [`doc/en/wasm/dev/server-hosting.md`](doc/en/wasm/dev/server-hosting.md)). It remains optional: nothing may require it.
    * Any component or service you add must keep working when only the Ecosystem host is present; never take a hard dependency on the server being reachable.

2.  **Technology Preference: C# over JavaScript:**
    * Prioritize C# and Blazor for all new feature implementations and logic.
    * Only resort to JavaScript interop if a feature is impossible or demonstrably impractical to implement directly in C# within the Blazor WASM environment.
    * **Never wire new `<script src="...">` references into a host project** (`wwwroot/index.html`, `App.razor`). Host projects only bootstrap the framework and the PWA service worker.
    * New JavaScript ships as an asset owned by the component or library that needs it, using Blazor's Razor library static-asset pipeline so hosts pick it up automatically through `_content/{Library}/...` — never through manual host references:
        * Component-scoped: `MyComponent.razor.js` next to `MyComponent.razor`, loaded via `IJSObjectReference` from that component only.
        * Shared across a library: a plain file under that Razor Class Library's `wwwroot/` (e.g. `Shared/HackerOs.AppSdk.Blazor/wwwroot/download.js`), imported by the C# code that needs it.
    * Keep JavaScript minimal, well-documented, and limited to browser APIs C# cannot reach — domain/business logic stays in C#.

---

## II. DEVELOPMENT PHILOSOPHY & STYLE:

1.  **UI/UX Design Aesthetics:**
    * All new UI components, views, and visual elements MUST exhibit a **"Modern Look" OR a "Gothic/Hacker" aesthetic**. Strive for visually distinct, thematic, and polished designs.
    * Avoid generic, unstyled, or purely functional appearances. Consider elements like color schemes (e.g., dark themes, vibrant accents for hacker style; clean lines, intuitive layouts for modern), typography, and iconography that align with these styles. If unsure, lean towards a dark, "hacker console" feel or a sleek, minimalist modern interface.
    * **Tools**: Use MudBlazor (see ADR 0016 / [`doc/en/wasm/dev/old/platform-ui-library.md`](doc/en/wasm/dev/old/platform-ui-library.md)) only for complex interaction surfaces — menus, data grids, tabs, validated forms, dialogs, and comparable selectors — ideally behind a Platform-owned wrapper. Native Blazor and scoped CSS remain required for desktop, window chrome, taskbar, launcher layout, and simple controls. MudBlazor types must never appear in App Abstractions, App SDK contracts, Simulation Abstractions, Platform Core, or Browser Infrastructure.

2.  **Collocated Component Assets — No Inline CSS/JS in Markup:**
    * **MANDATORY:** All CSS and JavaScript for a Blazor component **MUST** live in scoped, collocated files next to that component, not inline in the `.razor` markup. For a component `MyComponent.razor`: styles go in `MyComponent.razor.css`, component-scoped script goes in `MyComponent.razor.js`, and a non-trivial code-behind goes in `MyComponent.razor.cs`.
    * **PROHIBITED:** inline `style="..."` attributes, `<style>` blocks, `<script>` blocks, and raw JS event attributes directly in `.razor` markup when a scoped file can hold them instead. This is enforced at build time (ADR 0007) — inline styles/scripts fail the build. The one tolerated form is a bodiless `<script src="...">` reference, which an ASP.Hack view uses to link its own `wwwroot/js` files (ADR 0083).
    * Only truly global styles belong in the shared global CSS (design tokens, resets, fatal boot-error states); everything else stays scoped to its component.
    * This applies only to component markup — root host documents (`OS/HackerOs.Ecosystem/wwwroot/index.html`, `test/test/Components/App.razor`) are the framework bootstrap boundary and are exempt.

3.  **File Naming and Reusability:**
    * All new file names (and folder names you introduce) **MUST** be in English, regardless of the language used in conversation or commit messages.
    * If a piece of functionality could plausibly be reused by another app, page, or feature, **do not build it inline**: extract a Razor component (with its own collocated `.razor.css`/`.razor.js`) or a shared service in the appropriate `Shared/` or `Platform/` project instead of duplicating logic in the caller.

4.  **Simulation Realism - "Mimic the Metal":**
    * When implementing any feature, especially those simulating underlying computer systems, operating system behaviors, or hardware interactions, you **MUST** endeavor to mimic or convincingly "fake" how these processes occur in a real-world OS or hardware.
    * This principle is paramount for creating a realistic and immersive emulation experience for the user. Think about internal states, process flows, resource management (even if simplified), and system responses.

---

## III. WORKFLOW & COLLABORATION PROTOCOLS:

1.  **Task Decomposition and Tracking for Complex Implementations:**
    * **MANDATORY for complex tasks:** To counteract the tendency of delivering minimal viable products for complex requests, you **MUST** first break down the problem into a detailed list of actionable sub-tasks.
    * Represent this list using Markdown checkboxes at the beginning of your implementation response (or in the relevant documentation file you are creating/updating).
        * Example:
            ```
            Okay, I will implement the new virtual file system. Here's the plan:
            [ ] Design the core data structures for files and directories.
            [ ] Implement C# classes for `VirtualFile` and `VirtualDirectory`.
            [ ] Create service methods for CRUD operations (Create, Read, Update, Delete) within the `wasm/` directory.
            [ ] Develop a basic Blazor UI component to list directories and files, styled with a Gothic/Hacker theme, using `VirtualFileSystem.razor.css`.
            [ ] Add methods to simulate file access permissions.
            ```
    * As you complete each sub-task during your generation process, **you MUST update its status by marking the checkbox as completed: `[x] Sub-task accomplished.`** This provides clear progress tracking for complex requests.

2.  **Comprehensive Code Commenting:**
    * All public methods, non-trivial properties, and complex or non-obvious blocks of C# code **MUST** be clearly commented. Explain the *why* and *how* of the code.
    * Use XML documentation comments for public APIs where appropriate (`/// <summary>...</summary>`).

3.  **Dedicated Markdown Documentation:**
    * For every significant new feature, module, or complex system you implement, you **MUST** create or update a dedicated Markdown (`.md`) file.
    * This documentation resides under `doc/` at the repository root, **in two languages with the same folders and file names**: French (the original) in `doc/fr/wasm/` and English (its translation) in `doc/en/wasm/`. In each language: OS development docs (architecture, subsystems, build, tests, plans, pitfalls) in `dev/`, docs for building apps and sites on the framework (SDK, app model, ASP.Hack) in `framework/`, and project memory (state of the series, conventions, next free ADR number) in `memory/`. Architecture decisions are recorded as an ADR under `dev/adr/` when applicable. **Every doc change updates both `doc/fr/` and `doc/en/` in the same change.** Root-level readmes follow the same rule: `README.md`/`readme.md` in English, `readme-fr.md` in French, each linking to the other on its first line.
    * **Content of Documentation:**
        * **Purpose:** What the feature does.
        * **Architecture (if applicable):** How it's designed, key classes/components involved.
        * **Usage/API:** How to use it (e.g., public methods, Blazor component parameters).
        * **Key Decisions:** Any important design choices made.
        * **Task List (copy or link to the completed task list you created).**
    * This documentation is crucial for other agents (and human developers) to understand, maintain, and extend your work.
    * [`doc/en/wasm/dev/plans/ecosystem-rebuild-plan.md`](doc/en/wasm/dev/plans/ecosystem-rebuild-plan.md) is the current, authoritative direction for the ecosystem (validated 2026-10-10) — read it first. Documents written before that plan live in `doc/en/wasm/dev/old/` (including `implementation-status.md` and `integration-task-list.md`); they remain useful technical background but are no longer the source of direction.

4.  **Documentation Is Part of "Done" — Not Just for New Features:**
    * **MANDATORY:** Before considering any change complete, check whether it makes an existing doc under `doc/en/wasm/` wrong or incomplete — a changed behavior, a renamed contract, a superseded decision, a new host/project — and update that doc in the **same** change. Creating a doc for a brand-new feature is not enough if the change also silently invalidates something already written elsewhere.
    * If a change contradicts an accepted ADR, do not leave the ADR silently wrong: either the change is out of scope until a new ADR supersedes it, or a new ADR must be added recording the supersession (never edit an accepted ADR's decision in place — add a new one that references it).
    * If you are not sure a doc needs updating, search `doc/en/wasm/` (including `doc/en/wasm/dev/adr/`) for the feature/contract name before finishing, rather than assuming no doc mentions it.
    * A change that touches public API surface, manifests, capabilities, settings, or host composition and ships with no doc/ADR update should be treated as incomplete, the same way shipping with no tests would be.
    * [`doc/en/wasm/README.md`](doc/en/wasm/README.md) is the index of every doc in that folder (archived docs are indexed by `doc/en/wasm/dev/old/README.md`). Adding, renaming, or removing a doc **MUST** update that index in the same change. Every folder under `doc/` has a `README.md` that indexes its content (for example `doc/en/wasm/dev/README.md`, `doc/en/wasm/framework/README.md`, `doc/en/wasm/dev/adr/README.md`): the same change also updates the `README.md` of the folder that holds the doc.
    * [`doc/en/wasm/features.md`](doc/en/wasm/features.md) (French original: [`doc/fr/wasm/features.md`](doc/fr/wasm/features.md)) is the product overview linked from the root readme. Every PR to `main` that ships, changes or plans a feature **MUST** update it in both languages: move a shipped feature from "In development" to "Available features", add a row for a new feature ADR or `TODO` issue, and replace an ADR's PR link by the ADR once merged. The PR template ([`.github/pull_request_template.md`](.github/pull_request_template.md)) has a checkbox for it.

---

**Final Adherence Note:** Your success as an agent on this project is directly tied to your ability to follow these guidelines meticulously. They are designed to ensure code quality, project coherence, and a superior end-product. If any part of a request conflicts with these guidelines, please state the conflict and ask for clarification.