# HackerOS Build-Known Lazy Loading Architecture

## Overview

To optimize initial WebAssembly boot time and minimize initial PWA download payload, HackerOS implements **Build-Known Lazy Assembly Loading**.
Core platform framework assemblies load eagerly during boot, while non-critical application assemblies load on-demand when launched.

---

## 1. Assembly Classification

| Assembly | Loading Strategy | Justification |
|---|---|---|
| `HackerOs.App.Abstractions.dll` | **Eager** | Required for type system, manifests, and capability checking. |
| `HackerOs.AppSdk.dll` / `HackerOs.AppSdk.Blazor.dll` | **Eager** | Base execution context & component contracts. |
| `HackerOs.Platform.Core.dll` / `HackerOs.Platform.Blazor.dll` | **Eager** | Desktop shell, process manager, VFS router, event bus. |
| `HackerOs.AppSdk.Icons.dll` | **Eager** | Referenced directly by `HackerOs.Ecosystem` for `IIconCatalog` DI registration; see ADR 0026 for why this isn't (yet) declared lazy despite its ~2.3 MB brotli size. |
| `HackerOs.Ecosystem.dll` | **Eager** | Host composition root & PWA shell. |
| Browser, Calculator, Code Editor, Error Log Viewer, File Explorer, Hack Paint, Icon Viewer, Settings, System Monitor, Terminal, and Text Editor | **Lazy** | Their canonical manifests are embedded in the host catalog and each assembly loads on first launch. |
| Sample applications | **Not selected** | Samples remain test and SDK examples rather than installed system apps. |

---

## 2. MSBuild Configuration (`HackerOs.Ecosystem.csproj`)

Lazy assemblies are declared in the WebAssembly host project file:

```xml
<ItemGroup>
  <BlazorWebAssemblyLazyLoad Include="HackerOs.Apps.Browser.dll" />
  <!-- The remaining first-party system app assemblies are declared here too. -->
  <BlazorWebAssemblyLazyLoad Include="HackerOs.Apps.TextEditor.dll" />
</ItemGroup>
```

The same project embeds each system app's canonical `app.manifest.json`.
`BuildKnownLazyApps` deserializes those resources into the immutable runtime
catalog, avoiding a second hand-maintained manifest model in the host.

---

## 3. Dynamic Runtime Loading & Discovery

`WebAssemblyLazyAssemblyTransport` invokes Blazor's
`LazyAssemblyLoader.LoadAssembliesAsync` through `BuildKnownAssemblyLoaderRegistry`.
`BuildKnownLazyAppDescriptorRegistry` then validates the requested manifest
against exactly the assembly loaded for that app and makes its descriptor available to
`AppLifecycleOrchestrator` before launch. Both assembly loading and descriptor
discovery coalesce concurrent first-launch requests; unknown app IDs and assets
remain typed recoverable outcomes. Published-browser offline/reload evidence
remains an open audit item.

---

## 4. Offline Failure Handling & Recovery UI

The registry returns `Missing`, `Cancelled`, and `Failed` outcomes without
destabilizing the shell. The shell-level recoverable UI and offline cached-load
browser matrix remain open audit items.

Since ADR 0162, a load that fails because the origin now serves another build
(rebuild or deployment under an open tab) returns
`BuildKnownAssemblyLoadStatus.HostBuildChanged`; `HostBuildUpdateMonitor`
then raises the host's "update ready" notice, whose button restarts the OS on
the new build. See [`../adr/0162-restart-after-a-host-build-change.md`](../adr/0162-restart-after-a-host-build-change.md).

## Task Status

- [x] Embed all ten first-party system manifests in the runnable host catalog.
- [x] Declare all ten first-party system assemblies as build-known lazy assets.
- [x] Coalesce first-load requests and register a validated descriptor with the
  app lifecycle before launch.
- [x] Reconcile the selected in-memory catalog into persistent browser storage
  during boot.
- [ ] Prove published-browser first fetch, offline cached launch, reload, and
  corrupt/missing-asset recovery for every lazy app.
