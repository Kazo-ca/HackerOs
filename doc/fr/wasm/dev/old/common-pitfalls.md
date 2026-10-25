# Common Pitfalls

Recurring mistakes this codebase has already made at least once, each expensive
to re-diagnose from scratch. Read this before touching static web assets,
`AppManifest`, the IndexedDB schema, an E2E test harness, or ARIA attributes in
Razor markup. Every entry below was found and fixed for real in this project;
treat a fresh occurrence as a regression, not a surprise.

## 1. A referenced Blazor WASM "app" project's static assets are unprefixed, not `_content/{Project}/...`

`OS/HackerOs.Ecosystem` is a `Microsoft.NET.Sdk.BlazorWebAssembly` **app**, not
a Razor Class Library, but `test/test` and `Server/HackerOs.Server` both
`ProjectReference` it to reuse its component tree (ADR 0027). When a project
reference is consumed this way, ASP.NET Core's static web assets pipeline
exposes Ecosystem's own `wwwroot`-relative paths **unprefixed** at the
composed host (e.g. `wwwroot/css/app.css` → route `css/app.css`), unlike a
true Razor Class Library, whose assets get the `_content/{Library}/...`
prefix you'd expect. `@Assets["app.css"]` and `_content/HackerOs.Ecosystem/css/app.css`
are both wrong keys for this file; the correct one is `css/app.css` (or
`@Assets["css/app.css"]` if you want fingerprinting).

> **Update (v3/6, ADR 0098 §2):** `wwwroot/css/app.css` moved to the Razor Class
> Library `OS/HackerOs.Ecosystem.Host`, so every host (the Ecosystem PWA included)
> now serves it at `_content/HackerOs.Ecosystem.Host/css/app.css`, the normal RCL
> prefix. The unprefixed-route behaviour below still applies to anything left in
> the Ecosystem app's own `wwwroot` (service worker, web manifest, icons), and the
> rule about decoy copies is unchanged.

**Why this bites hard:** the wrong key doesn't error — if the host also has
its own file at that literal path (e.g. a leftover `dotnet new blazor`
scaffold `wwwroot/app.css`), the lookup silently resolves to *that* instead,
and the host renders with no error, no missing-file warning, just visibly
wrong styling (or worse, only subtly wrong colors from CSS custom-property
fallbacks). This is exactly what happened: `test/test` and
`Server/HackerOs.Server` each carried their own unmodified scaffold
`wwwroot/app.css`, so `@Assets["app.css"]` "worked" and matched the decoy file
for the life of both hosts, and neither host ever rendered the real
`--hos-*` design tokens. See `webassembly-debugging.md` for the full writeup
and the fix.

**Rule:** never give a host project (`test/test`, `Server/HackerOs.Server`) its
own physical copy of an asset that's supposed to come from a referenced
project — a decoy file is what makes the wrong lookup key look correct. If you
add a fourth host, verify its static-asset route by checking the referenced
project's own `*.staticwebassets.endpoints.json` in its build output, not by
guessing the URL from convention.

## 2. `AppManifestJsonSerializer`'s allowlist must be updated whenever `AppManifest` gains a property

`AppManifestJsonSerializer.DeserializeStrict` hand-validates every top-level
(and several nested) property name against a hardcoded allowlist before
deserializing (`ValidateObject`, `AppManifestJsonSerializer.cs`). This is by
design (ADR 0010) — it exists to reject typos and drift in manifest JSON — but
it means the allowlist is a second place, separate from the `AppManifest`
record itself, that must be kept in sync by hand.

ADR 0040 added `AppManifest.DeclaredTopicPermissions` (JSON:
`declaredTopicPermissions`) without updating this allowlist, so
`DeserializeStrict` rejected the canonical manifest fixture — and would reject
any real manifest using the field — with `Unknown property
'declaredTopicPermissions' at 'manifest'`. `HackerOs.App.Abstractions.Tests`
didn't catch it because none of its tests round-trip the canonical fixture
through `DeserializeStrict`; only `HackerOs.Infrastructure.Browser.Tests`
happened to exercise that path.

**Rule:** any change that adds, renames, or nests a new `AppManifest` property
must add the matching entry to `ValidateObject`'s allowlist (and, for a
nested object/array, a `case` in the `switch` with its own allowed sub-keys)
in the same change. After adding a manifest property, round-trip the
canonical fixture (`Schema/Fixtures/app-manifest.canonical.json`) through
`AppManifestJsonSerializer.DeserializeStrict` — by hand or via a test — before
considering the change done.

## 3. Never hardcode an IndexedDB schema version literal — always reference `HackerOsIndexedDbSchema.CurrentVersion`

Every real repository in `Infrastructure/HackerOs.Infrastructure.Browser`
correctly threads `HackerOsIndexedDbSchema.CurrentVersion` through
`IndexedDbInteropAdapter.OpenAsync`/`ExecuteAsync`. But
`Tests/HackerOs.BrowserHarness.Tests/App.razor`'s `VerifyOrphanCleanupAsync`
bypasses the adapter and calls the JS module's `executeTransaction` directly,
with the database version hardcoded as the literal `2` — stale from before
the schema was bumped to `4` (ADR 0028/0029) and never updated. The JS
module's `connections` cache is keyed by `` `${databaseName}:${version}` ``,
so a call with the wrong version doesn't find the already-open connection at
version 4 and throws `IndexedDB database 'hackeros' version 2 is not open.`

**Why this was hard to isolate:** the failure is deterministic but every
plausible-sounding cause is a dead end — the physical database really is at
version 4 (confirmed via `indexedDB.databases()`), a full `bin`/`obj` wipe and
clean rebuild changes nothing, and the browser's own stack trace mixes in
stale URLs from unrelated ports (a red herring from Chrome's cross-navigation
compiled-code cache, not the actual bug). The only way to find it was to
`grep` for the JS interop method name `"executeTransaction"` as a C# string
literal across the whole repo, not just the schema/adapter files — this
surfaces every call site, including ones that don't go through the adapter.

**Rule:** if you ever need to call `indexedDb.js` directly instead of through
`IndexedDbInteropAdapter`, pass `HackerOsIndexedDbSchema.CurrentVersion`, never
a literal. If a mysterious "database version N is not open" error shows a
version that doesn't match `CurrentVersion`, `grep -rn '"executeTransaction"'`
(or `"openDatabase"`) across the whole repo before assuming it's a build- or
browser-cache problem.

## 4. `dotnet run` for an E2E test harness needs `ASPNETCORE_ENVIRONMENT=Development` set explicitly

Several E2E suites (`HackerOs.E2E.Tests`, `HackerOs.UI.E2E.Tests`) spawn a
host project as a child `dotnet run` process via `Process.Start`. A child
process inherits environment variables from whatever shell invoked `dotnet
test` — in an IDE that's often `Development` by convention, but in a plain
shell (and in CI) `ASPNETCORE_ENVIRONMENT` is usually unset, so the spawned
host defaults to `Production`.

In `Production`, `app.MapStaticAssets()` still wires up
`StaticAssetDevelopmentRuntimeHandler` (despite the name), which expects every
static web asset to resolve to a physical file under the host's own
`wwwroot` for its dev-time patching checks. Assets composed from a referenced
project or NuGet package (MudBlazor, `HackerOs.Ecosystem`,
`HackerOs.Platform.Blazor`) aren't physical files under the host's `wwwroot`
— every one of them 500s, and the harness never finishes booting. From the
test's side this looks like `net::ERR_CONNECTION_REFUSED` or "the browser
harness did not become ready" — nothing points at the environment variable.

**Rule:** any `ProcessStartInfo` that spawns `dotnet run` (or `dotnet
exec`/published binary) for one of the three host projects as a test harness
must explicitly set `startInfo.Environment["ASPNETCORE_ENVIRONMENT"] =
"Development"`. Don't rely on the invoking shell's environment. See
`HackerOs.UI.E2E.Tests/E2ESupport.cs`'s `StartHarness` for the fix.

## 5. E2E test harnesses launched with `--configuration Release` need a Release build first

`HackerOs.E2E.Tests` and `HackerOs.UI.E2E.Tests` both spawn their harness with
`--configuration Release`. `dotnet build HackerOs.sln -c Debug` alone leaves
every Release output directory empty, so the spawned process crashes
immediately with `The static resources manifest file '...staticwebassets.
endpoints.json' was not found` — which, again, surfaces to the test as a
`ERR_CONNECTION_REFUSED`/"harness did not become ready" timeout, not as a
build error.

**Rule:** before running any E2E suite locally, build both configurations:
`dotnet build HackerOs.sln -c Debug` (for the test project itself) and
`dotnet build HackerOs.sln -c Release` (for the spawned harness). CI presumably
already does this as part of its normal build matrix; a fresh local clone or
worktree will not have a Release output directory yet.

## 6. Don't run the E2E/UI-E2E/PWA-E2E suites in parallel against each other

`dotnet test HackerOs.sln` runs every test project's suite concurrently by
default. The three Playwright-based suites (`HackerOs.E2E.Tests`,
`HackerOs.UI.E2E.Tests`, `HackerOs.Pwa.E2E.Tests`) each spawn their own
`dotnet run`/`dotnet publish` child processes and reserve their own ports —
running all three at once under `dotnet test HackerOs.sln` produces spurious,
non-reproducible failures (`dotnet publish` failing with exit code 1,
connection-refused harness boots) purely from resource contention, not from
anything wrong in the code under test. The same tests reliably pass when run
one project at a time.

**Rule:** when investigating an E2E failure, always reproduce it by running
that one test project's suite in isolation
(`dotnet test Tests/HackerOs.E2E.Tests -c Debug --no-build`, etc.) before
concluding it's a real regression. If `dotnet test HackerOs.sln` reports E2E
failures but the same tests pass individually, that's resource contention in
the environment, not a bug in the app — filter E2E projects out of routine
solution-wide test runs (`--filter "FullyQualifiedName!~E2E.Tests&
FullyQualifiedName!~Pwa.E2E"`) and run them separately.

## 7. Test projects don't run the trim analyzer: a shipping app is the only place IL2110/IL2111 surface

`Directory.Build.props` turns on `IsTrimmable`/`EnableTrimAnalyzer` (warnings
as errors) for every project whose name does not end with `.Tests`. ASP.Hack
views were built and tested for weeks inside test projects only; the first
app under `Apps/` with views (the Intranet sample, web/11, ADR 0087 §2) failed
to build with `IL2110`/`IL2111` in the generated route table and in every
Razor-generated view.

Why: a view type is named with `DynamicallyAccessedMemberTypes.All` (the
generated view table, `RazorCompiledItemAttribute`, `[Layout(typeof(...))]`).
`All` makes every member of the type **and of its base classes** "accessed by
reflection", so any `[DynamicallyAccessedMembers]` on a base-class member (a
property, or a parameter that an `async` method captures into its state
machine field) is reported in every consuming project.

**Rule:** no member of a view or component base class (`HackViewBase`,
`HackRazorPage`, `HackComponentBase`, ...) carries a DAM annotation; keep it on
the OS methods that receive a `Type`, and keep annotated parameters out of
`async` methods (do the reflection in a synchronous prologue). When you add a
base type to an SDK library, build a shipping project that uses it (the
Ecosystem host builds the samples), not just its test project.

## 8. Never bind an ARIA state attribute straight to a `bool`

Blazor renders a `bool` attribute value the HTML way: `true` writes the
attribute with an **empty** value and `false` drops it. That is right for
`disabled` or `checked`, but wrong for ARIA states, whose values are the
strings `"true"`/`"false"`: `aria-checked="@isOn"` renders `aria-checked=""`
(invalid, so axe reports `aria-required-attr` on a `role="radio"`) and nothing
at all when off (a toggle button that loses `aria-pressed` is no longer a
toggle for a screen reader). `HackerOs.UI.E2E.Tests` caught this in Settings,
the Code Editor and the App Launcher (fixed on `rebuild/5-ui-a11y-mkdir`,
2026-10-16, 37 attributes).

**Rule:** spell the value out — `aria-pressed="@(isOn ? "true" : "false")"`,
or a `string?` property when the attribute must disappear in some states
(`FileViewTreeNode.AriaExpanded`: `null` for a file, which has no
`aria-expanded`). This applies to `aria-checked`, `aria-selected`,
`aria-pressed`, `aria-expanded`, `aria-invalid`, `aria-busy`,
`aria-multiselectable`, and every other `true`/`false` ARIA state.

## 9. An app that won't open after a rebuild is a stale page, not a broken build

Lazy-loaded apps fetch `_framework/HackerOs.Apps.<Name>.<fingerprint>.wasm`
with the fingerprint the page's `dotnet.js` knew at boot. Rebuild while a tab
stays open (F5 in Visual Studio, hot reload, a deployment) and that file is
gone or has another hash: the launch fails with
`download '…/HackerOs.Apps.Settings.yesfevpgxl.wasm' failed 0 TypeError: Failed to fetch`
(an SRI integrity error in the console). The build itself is fine.

**Since ADR 0162** the OS notices it: the failed launch says "HackerOS was
updated while it was running", and the "A HackerOS update is ready" notice
restarts on the new build. **By hand:** stop the app, start it again, then
Ctrl+F5 in the tab (or F12 ▸ Application ▸ Clear site data). With
`dotnet run`, restart the server after rebuilding: a dev server left running
serves a mix of both builds and the page fails at boot.
