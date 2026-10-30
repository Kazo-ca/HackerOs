> [Version française](../../../fr/wasm/dev/trimmed-publish.md)

# Trimmed Release publication

## Purpose

The Release publication of the Ecosystem host (`OS/HackerOs.Ecosystem`) is
trimmed by ILLink (work item `v3/14-trimming`, ADR 0106 and 0113): the .NET
framework code that HackerOS does not use is removed from the WebAssembly
files. The first load downloads less, and each Web Worker (ADR 0070) starts a
smaller runtime.

| Ecosystem publication | `.wasm` files | Size | Brotli |
|---|---|---|---|
| before (`PublishTrimmed=false`) | 273 | 50.9 MiB | 13.5 MiB |
| after (trimmed) | 134 | 26.5 MiB | 8.0 MiB |

Measured on 2026-10-17 with `build/publish-trimmed.sh --compare` (files of the
`fr/` satellites included). AOT is out of scope.

## Architecture

```
Ecosystem.csproj            PublishTrimmed=true, TrimMode=partial
        │
        ├─ CollectHackerOsBuiltInApplications (build/HackerOs.Apps.targets)
        │     ManifestGenerator aggregate --trimmer-roots
        │       → obj/.../BuiltInApplications.json
        │       → obj/.../HackerOs.TrimmerRoots.xml  ──► TrimmerRootDescriptor
        │
        └─ _HackerOsRootHackerOsAssemblies (avant PrepareForILLink)
              chaque HackerOs.*.dll publiée ──► TrimmerRootAssembly
                                    │
                              ILLink (publish)
            framework et bibliothèques élaguables : élagués
            assemblies HackerOS et apps : entières
            paquets non élaguables (MudBlazor…) : entiers (partial)
```

- **Why roots.** The OS never calls an app's code directly: it loads its
  assembly on demand (`BlazorWebAssemblyLazyLoad`) and finds its entry point,
  its embeddable components and a site's Blazor root through the manifest names
  (`Assembly.GetType`, `Activator.CreateInstance`, loading by name in the
  workers). Without a root, ILLink removes these assemblies.
- **Why whole assemblies.** HackerOS code passes its records to JavaScript
  interop, which serializes them by reflection without the trimming analysis
  seeing it. Member-by-member trimming breaks startup (storage "unavailable")
  for only 0.4 MiB of Brotli gained (ADR 0113 §1).
- **`HackerOs.TrimmerRoots.xml`** (`TrimmerRootDescriptorWriter` of the manifest
  generator): one `<assembly fullname="…" preserve="all" />` line per assembly
  that a manifest names (entry point, per-platform entries, components,
  `web.blazor`). It covers apps installed as packages, even when their name does
  not start with `HackerOs.`.
- **Warning suppressions.** The remaining `UnconditionalSuppressMessage`
  attributes (app discovery, workers, embeddable components, Blazor site router,
  MVC model binding) cite the root that makes them safe.

## Usage

### Publishing and testing

```bash
wasm2/HackerOs/build/publish-trimmed.sh            # publie, mesure, sert, teste
wasm2/HackerOs/build/publish-trimmed.sh --compare  # publie aussi sans trimming et compare
wasm2/HackerOs/build/publish-trimmed.sh --skip-build
```

The script publishes to `artifacts/publish-trimmed/publish`, fails on any `IL…`
warning, serves the publication on `127.0.0.1` and `127.0.0.2`, then runs
`Tests/HackerOs.TrimmedPublish.E2E.Tests`:

| Test | What it checks |
|---|---|
| `Trimmed_publish_runs_every_app_a_worker_command_and_both_sample_sites` | profile creation; each windowed app of the Start menu (read from `BuiltInApplications.json`, administrator approval included) opens its window without error; `seq 30 \| grep 1 \| wc -l` and `/proc/hackeros/workers` in the terminal; the Intranet (`.cshtml` view, script, REST API, Blazor table) and the sample Blazor site (counter, link, API) in the browser |
| `Trimmed_publish_boots_offline_after_the_first_load` | on `127.0.0.2` (the service worker is not registered on `127.0.0.1`): version caching, reload without network, login, launch of a never-loaded app |
| `The_calculator_rendered_in_a_worker_works_like_on_the_page` | ADR 0133 trial, setting enabled by rewriting `appsettings.json`: the Calculator in a worker computes with mouse and keyboard, keeps its CSS, `Tab`, axe, `Alt+F7`/`Alt+Q`, dedicated worker released on close ([`remote-window-rendering.md`](remote-window-rendering.md)) |
| `Measures_the_calculator_on_the_page_and_in_a_worker` | ADR 0133 measurements (opening, latency, memory, batches), written to `remote-window-measurements.json` in the artifacts folder |
| `Trimmed_publish_shows_unread_notifications_next_to_the_clock` | ADR 0196: a new account gets the what's new notification (`whats-new.json` served); clock dot and tooltip, no dot on Applications, opened panel = read, **View** opens Settings ▸ What's new and dismisses the notification; screenshots `trimmed-clock-dot.png` and `trimmed-whats-new-page.png` |

The script requires all five tests to pass (`EXPECTED_TESTS`).

No console error is allowed. Without `HACKEROS_TRIMMED_URL`, the suite is
skipped (a `dotnet test` of the solution does not run it).

This test is part of the local merge gate of `v3/14` and of the following work
items (ADR 0106 §5). `build/verify-hosts.sh` also publishes the Ecosystem in
Release: its startup test therefore also covers the trimmed publication.

### A host from another repository

The `hackeros-host` template publishes trimmed in the same way. The
`HackerOs.App.Build` targets generate the roots and keep the `HackerOs.*`
packages whole; app templates start with `IsTrimmable=true`, and the package
enables the trimming analysis in an app project that does not set it.

### Pitfall: successive publications

The SDK's WebCIL conversion is incremental by file dates. A non-trimmed
publication (`-p:PublishTrimmed=false`) made after a trimmed publication in the
same `obj/` folder may pick up the trimmed assemblies. Before comparing, delete
`OS/HackerOs.Ecosystem/obj/Release/net10.0/linked`, `webcil` and `tmp-webcil`
(the script does it).

## Key decisions

- `TrimMode=partial` (ADR 0106 §1): packages that do not declare themselves
  trimmable stay whole.
- HackerOS and app assemblies kept whole; only the framework is trimmed
  (ADR 0113 §1-2).
- No generated table for the `[Route]` pages of a Blazor site: the site's
  assembly is whole (ADR 0113 §3).
- Offline test on `127.0.0.2` rather than `[::1]` (ADR 0113 §4).

## Tasks

- [x] `PublishTrimmed=true` and `TrimMode=partial` in the Ecosystem and the host template.
- [x] `HackerOs.TrimmerRoots.xml` generated by the aggregation and passed to ILLink.
- [x] `HackerOs.*` assemblies kept whole when publishing a host.
- [x] Warning suppression justifications updated.
- [x] `HackerOs.App.Build`: trimming analysis in app projects; templates `IsTrimmable=true`.
- [x] `build/publish-trimmed.sh` and `Tests/HackerOs.TrimmedPublish.E2E.Tests`.
- [x] Sizes before and after measured (here and in `building-and-testing.md`).
- [x] ADR 0113.
