> [Version française](../../../../fr/wasm/dev/adr/0113-trimming-implementation-adjustments.md)

# ADR 0113: adjustments to the trimmed publish

## Status

Accepted on 2026-10-17 (work item `v3/14-trimming`). One question remains open to
Benoit in the work item thread (§1); what follows applies the
recommended answer while awaiting it.

- **Adjusts:** ADR 0106 §1, §2 and §3 (trimmed Release publish of the Ecosystem
  host).
- **Document:** [`trimmed-publish.md`](../trimmed-publish.md).

## Context

ADR 0106 planned to trim "our" assemblies (those marked
`IsTrimmable`) while keeping third-party packages whole, and to keep in each
app only the types that the OS reaches by name (entry point, `AppDefinition`,
embeddable components, controllers, views, routable components).

The implementation showed three things:

1. **`TrimMode=partial` also trims the framework.** The .NET and
   ASP.NET Core assemblies carry the `IsTrimmable` attribute: they are the heavy ones.
   On the Ecosystem, measured with `build/publish-trimmed.sh --compare`:

   | Publish | `.wasm` files | Size | Brotli |
   |---|---|---|---|
   | not trimmed | 273 | 50.9 MiB | 13.5 MiB |
   | trimmed, HackerOS assemblies trimmed member by member | 128 | 25.1 MiB | 7.6 MiB |
   | trimmed, whole HackerOS assemblies (chosen) | 134 | 26.5 MiB | 8.0 MiB |

   Trimming our own assemblies only gains 1.4 MiB (0.4 MiB in Brotli):
   their weight is mostly resources (icons, embedded `wwwroot` of
   sites, translations).
2. **Trimming our assemblies breaks startup, without any warning.** The
   code passes its own records to the JavaScript interop
   (`IJSRuntime.InvokeVoidAsync(..., params object[] args)`), which serializes them
   by reflection. This parameter is not annotated for the trimmer: the analysis
   sees nothing, and the publish removes the constructor parameter names
   and the accessors that only serialization reads. First
   symptom: `IndexedDbMigrationPlan` no longer serializes
   (`ConstructorContainsNullParameterNames`) and the OS stops on "Local
   storage is unavailable". There are nearly 280 interop calls in the
   solution; fixing them one by one for 0.4 MiB is not worth the risk.
3. **Apps are referenced by no code.** Without a root, the trimmer
   removes all app assemblies, and the publish fails
   (`BLAZORSDK1001`, assemblies to lazily load not found).

## Decision

1. **All HackerOS assemblies stay whole.** A target of
   `build/HackerOs.Apps.targets` (`_HackerOsRootHackerOsAssemblies`, hosts
   only, when `PublishTrimmed` is `true`) adds each
   `HackerOs.*` assembly of the publish as a `TrimmerRootAssembly`. The framework and
   the other trimmable libraries are trimmed; non-trimmable packages
   (MudBlazor, AngleSharp…) stay whole (`TrimMode=partial`, unchanged). This
   replaces "only our assemblies are trimmed" of ADR 0106 §1. Moving to
   trimming our assemblies would first require making all JavaScript interop
   safe (generated serialization): a new ADR, with measurements.
2. **Generated roots: whole assemblies.** The manifest generator
   (`aggregate --trimmer-roots`) writes `HackerOs.TrimmerRoots.xml` next to
   `BuiltInApplications.json`; the host passes it to ILLink
   (`TrimmerRootDescriptor`). Each assembly named by a manifest (entry
   point, per-platform entries, components, Blazor root of a site) is
   kept whole (`preserve="all"`), not only the named types. This is what
   keeps an app from another repository that is not named `HackerOs.*` (§1 does not
   cover it). The `AppDefinition` class needs no root: the OS never
   instantiates it (the manifest comes from the JSON, a site's generated `WebHost`
   calls it from code). This replaces the detail of ADR 0106 §2.
3. **No generated table for the Blazor pages of sites.** ADR 0106 §3
   asked to replace `GetExportedTypes` where a generated table exists.
   The only remaining `GetExportedTypes` (`HackSiteRouter`, `[Route]` pages of a
   Blazor site) has no table: since the site assembly is kept whole, it
   remains safe, as Blazor's router is for the main assembly.
   MVC routes and views already went through the generated tables
   (ADR 0082, 0083, 0086). The remaining warning suppressions now cite
   this ADR instead of "PublishTrimmed is disabled".
4. **Publish test.** `build/publish-trimmed.sh` publishes, measures, serves
   the publish and runs `Tests/HackerOs.TrimmedPublish.E2E.Tests` (login,
   each windowed app in the menu, a command in a worker, the Intranet and the
   Blazor site, then offline startup). The service worker is registered
   neither on `localhost` nor on `127.0.0.1` (`index.html`): the offline test
   goes through a second server on `127.0.0.2`, a loopback address that
   Chromium treats as secure, as the PWA suite already does without IPv6.
5. **Incremental WebCIL conversion.** The SDK reconverts an assembly to
   WebCIL based on file dates: a non-trimmed publish done after
   a trimmed publish in the same `obj/` may pick up the trimmed
   files (observed: MudBlazor missing, CoreLib trimmed). The script starts from a clean
   state (`obj/.../linked`, `webcil`, `tmp-webcil`) before each
   publish; `building-and-testing.md` points this out to developers.

## Consequences

- The Ecosystem publish goes from 50.9 to 26.5 MiB of WebAssembly
  (13.5 to 8.0 MiB in Brotli). The first load downloads less, and each
  worker starts a smaller runtime.
- The feature switches of resource accounting (ADR 0066) do not
  remove code inside `HackerOs.Platform.ResourceAccounting`,
  kept whole; with both measurements turned off at build time, the assembly is
  still not published.
- The `hackeros-host` template publishes trimmed the same way (`PublishTrimmed`,
  `TrimMode=partial`); app templates ship with `IsTrimmable=true`, and
  `HackerOs.App.Build` enables the analysis in an app project that does not set it
  itself (ADR 0106 §4, unchanged).
- `verify-hosts.sh` publishes the Ecosystem as before: it therefore now tests
  the trimmed publish.
