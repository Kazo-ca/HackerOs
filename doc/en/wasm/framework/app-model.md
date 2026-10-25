> [Version française](../../../fr/wasm/framework/app-model.md)

# App model: `AppDefinition` and generated manifests

Phase 2 of the [rebuild plan](../dev/plans/ecosystem-rebuild-plan.md) (sections 6.1 and 6.2),
decisions of [ADR 0047](../dev/adr/0047-app-definition-and-generated-manifests.md).

## Goal

An app's manifest has a single source: a C# class marked
`[AppManifest]`. The build derives the app's `manifest.json` from it, then the host
aggregates all manifests into `BuiltInApplications.json`. Installing an app
in the host comes down to a `ProjectReference`.

## `AppDefinition` and `[AppManifest]`

Project `Shared/HackerOs.App.Abstractions`, folder `Definition/`, namespace
`HackerOs.App.Abstractions`.

| Type | Role |
| --- | --- |
| `AppManifestAttribute` | Marks the class that describes an app. Single use, not inherited. Named property `Execution` (ADR 0070): where the OS runs the app (see [Execution host](#execution-host-adr-0070)). |
| `AppDefinition` | Abstract base class: 4 required members (`Id`, `Name`, `Kind`, `EntryPoint`) and optional members with a default value. |
| `FileHandler`, `TerminalCommand` | Short forms of `FileHandlerManifest` and `TerminalCommandManifest`. |
| `AppDefinitionMapper` | `ToManifest(definition)` converts a definition into an `AppManifest` (schema version 1). |

Default values of the optional members:

| Member | Default |
| --- | --- |
| `Version` | `1.0.0` |
| `Description` | `Name` |
| `PublisherId` | `pub.local` |
| `Category` | `utilities` |
| `LaunchVisibility` | `Visible` |
| `Icons`, `Capabilities`, `FileHandlers`, `Dependencies`, `Intents`, `DeclaredTopicPermissions`, `Localizations`, `Assets` | empty list |
| `Terminal`, `MobileEntryPoint`, `Settings` | `null` |
| `[RegisterFileContextMenu]` | no method; `contextMenus` is then omitted from the manifest. A static method of the class itself, run twice at build time, registers the app's entries in file menus (ADR 0151, ADR 0159, [`context-menu.md`](context-menu.md#app-entries-in-the-file-menus-work-item-4)); titles in `Resources/AppManifest.resx` |
| `Components` | empty list; `components` is then omitted from the manifest (ADR 0063, [`embedded-components.md`](embedded-components.md)); `hackeros.context-menu` is refused (platform); `hackeros.file-view` and `hackeros.file-dialogs` are open to any windowed app, an administrator chooses the one in service (ADR 0156, ADR 0158) |
| `SingleInstancePerUser`, `AutoStart`, `SupportsBack` | `false` |
| `ServiceStartup` | `null` (= `Login`); `serviceStartup` is then omitted from the manifest. `Boot` only applies to a system app of kind `Service` (ADR 0097 §4, ADR 0107) |
| `OomScoreAdjust` | `null` (inherits; `-500` for a system service); `oomScoreAdj` is then omitted from the manifest. Negative only for a system app (`HOSAPP0024`, ADR 0126 §4, ADR 0142, [`oom-killer.md`](../dev/oom-killer.md)) |
| `Resources` | `AppResourceProfileManifest.None` |
| `Window` | `null`: the window takes `WindowOptions.Default` (800×560, minimum 360×240); the `window` object is then omitted from the manifest (ADR 0095, [`window-model.md`](window-model.md)). Declared only by an app of kind `Window` (`manifest.window.*`). |

Since ADR 0165, the fourteen windowed system apps declare
`MobileEntryPoint` with the same component as `EntryPoint`. The generator
therefore produces the `platform` form (`desktop`, `mobile`) for these apps; the
mobile drawer never bypasses a missing compatibility declaration.
Third-party apps remain desktop only as long as they do not declare
their mobile entry point.

Example:

```csharp
[AppManifest]
public sealed class TextEditorApp : AppDefinition
{
    public override string Id => "org.hackeros.text-editor";
    public override string Name => "Text Editor";
    public override AppKind Kind => AppKind.Window;
    public override Type EntryPoint => typeof(TextEditorWindow);
    public override Type? MobileEntryPoint => typeof(TextEditorWindow);
    public override string PublisherId => "pub.hackeros";
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md", ".log"], ["open", "edit"])];
}
```

### Conversion rules

- `EntryPoint` becomes `{ "assembly": "<assembly>.dll", "type": "<full name>" }`.
- A non-null `MobileEntryPoint` produces the `platform` form (`desktop` and `mobile`).
  If it is the same type as `EntryPoint`, a single entry covers both
  platforms; otherwise, one entry per platform.
- `Icons` becomes `presentation.iconAssetPaths`.
  For the chrome and the taskbar (ADR 0164), the first image is resolved under
  `_content/<assembly>/…`; the file must be in `wwwroot/` and declared in
  `Assets`. The windowed system apps each declare `icons/app.svg`, with
  a fingerprint computed at build time. macOS only hides the title bar
  icon; its dock keeps the app's icon.
- A null `TerminalCommand.Aliases` becomes an empty list.
- `sdkCompatibility` (ADR 0098 §3): by default, `minimumVersion` is the version
  of the SDK against which the app was compiled. The generator reads it from the
  `AssemblyInformationalVersion` of the `HackerOs.App.Abstractions.dll`
  referenced by the app (for example `2.0.0`, see
  [`sdk-packages.md`](sdk-packages.md)) and passes it to
  `ToManifest(definition, sdkMinimumVersion)`; `ToManifest(definition)` takes
  `HackerOsSdk.Version`, the version of the loaded SDK. An app that wants a range
  overrides `AppDefinition.SdkCompatibility` (minimum and optional maximum).
  At load time, `AppCatalog` refuses an app whose range does not accept the host's
  SDK (`catalog.sdk.incompatible`): same major version as the minimum,
  minimum ≤ SDK ≤ maximum (`HackerOsSdk.IsCompatible`).
- `osCompatibility` and `update` are not exposed by `AppDefinition`: they
  remain absent from the generated manifest.
- `[AppManifest(Execution = …)]` becomes `execution`, always written, resolved
  (never `default`): see the next section.

### Intents and URI schemes (ADR 0096)

- `Intents` (`AppIntentDeclarationManifest(IntentId, PayloadSchemaAssetPath)`)
  declares the intents the app **receives**. A free identifier must be well
  formed (`reversed.domain.name.vN`, `AppIntentIds.IsWellFormed`); the
  dispatcher routes to the installed and enabled apps that declare it, instead of
  answering `intent.unknown`. Declaring
  `org.hackeros.intent.show-app-settings.v1` means "I have my own settings
  page": the app is launched with `--show-settings`.
- A URI scheme is a media type `x-scheme-handler/<scheme>` in
  `FileHandlers`, as under freedesktop:
  `new("x-scheme-handler/https", [], ["open"])`. The browser declares `http` and
  `https`.
- The manifest schema does not change. Details: [`app-intents.md`](app-intents.md).

### Execution host (ADR 0070)

The `[AppManifest]` attribute says **where** the OS runs the app, through its
named property `Execution` (`AppExecutionHost`, `HackerOs.App.Abstractions`):

| Value | `execution` field | Where | Default for |
| --- | --- | --- | --- |
| `MainThread` | `mainThread` | the browser's main thread, with the desktop | windowed app (the only value allowed: it needs the DOM) |
| `Pool` | `pool` | a pool worker, one per virtual core; the steps of the same pipeline go to the same one | command |
| `Dedicated` | `dedicated` | a worker of its own, taken from the ready worker (ADR 0071) | service |
| `Web` | `web` | the `web` worker, shared by all sites (ADR 0073 §1) | web app (`AppKind.Web`, the only one that can use it) |
| `Default` | none | left as is in the attribute: the generator writes the kind's default | none |

```csharp
// ADR 0070: stays on the main thread: it acts on the calling shell (its working directory),
// like the cd builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class CdApp : AppDefinition { … }
```

- The mapper reads the attribute of the definition class and writes the resolved
  value (`AppExecutionHosts.Resolve`); a definition without an attribute (tests)
  gets the default of its kind.
- `AppManifest.Execution` is `null` for a manifest written before ADR 0070:
  `AppExecutionHosts.Of(manifest)` then gives the kind's default.
- The validator refuses `pool` or `dedicated` for a windowed app
  (`manifest.execution.window`) and the value `default`
  (`manifest.execution.invalid`); the schema and the whitelist of
  `AppManifestJsonSerializer.DeserializeStrict` know `execution`.
- A command or service that needs something a worker does not offer declares
  `MainThread`, **with the reason as a comment** above the attribute. In
  `HackerOs.Commands`: the shell built-ins (`cd`, `exit`, `export`,
  `unset`, `jobs`, `read`, `help`, `which`, `kill`, `sudo`, `watch`), `passwd`,
  `hackbackup`, `nice` and `renice` (ADR 0127), and the full-screen commands
  (`nano`, `top`, `htop`). The sample service and
  `hackwebd` are in a dedicated worker since ADR 0124. The other commands go
  to the pool (up-to-date list in [`worker-processes.md`](../dev/worker-processes.md)).
- The validator refuses `pool` and `dedicated` for a web app
  (`manifest.execution.web`) and `web` for any other app
  (`manifest.execution.web-only`). A web app declares its URLs through
  `AppDefinition.Urls` (field `web.urls`): see
  [`listening-ports-and-dns.md`](listening-ports-and-dns.md).
- A host without Web Workers ignores `execution` (ADR 0070 §6). There are no more
  in this repository: `HackerOs.Server` serves the OS in WebAssembly since
  [ADR 0130](../dev/adr/0130-server-serves-webassembly-os-and-api.md).

Details of the mechanism: [`worker-processes.md`](../dev/worker-processes.md).

### Deviation from the ADR text

ADR 0047 writes `SettingsSchema? Settings`. `SettingsSchema` lives in
`HackerOs.Simulation.Abstractions`, which `HackerOs.App.Abstractions` does not
reference. The property therefore uses the existing manifest record
`AppSettingsSchemaManifest?`, in accordance with the ADR's rule "rare cases
reuse the existing `*Manifest` records". The JSON content is the
same.

### Rules imposed on the class

Checked by the generator (see below): `public` class, non-abstract,
marked `[AppManifest]`, public parameterless constructor; `EntryPoint` and
`MobileEntryPoint` in the same assembly as the definition; pure getters (the
generator compares two instantiations); `AppManifestValidator` and the entry
point rules of `AppEntryPointDiscovery` pass.

## Generating `manifest.json` at build time

### Tool

`Tools/HackerOs.Tools.ManifestGenerator` (.NET executable, shared framework
`Microsoft.AspNetCore.App`) replaces the former `HackerOs.Tools.ManifestValidator`.

| Command | Role |
| --- | --- |
| `generate --assembly <dll> --project <csproj> --stamp <file> [--dependencies <list>]` | Called by MSBuild after an app is compiled. |
| `aggregate --project <host.csproj> --apps <list> --output <BuiltInApplications.json> [--trimmer-roots <roots.xml>]` | Called by the host (see below). |
| `validate <manifest.json>` | Strict reading (schema v1) and `AppManifestValidator` on a file; replaces the former validator. |

How `generate` works:

1. loads the compiled assembly into an isolated, collectible
   `AssemblyLoadContext` (`AppAssemblyLoadContext`). The shared framework assemblies
   and the tool's HackerOS contracts come from the default context (a single
   identity for `AppDefinition`); the others come from the list
   `@(ReferenceCopyLocalPaths)` + `@(ReferencePath)` then from the app's output
   folder. Reference assemblies (`[ReferenceAssembly]`) are ignored;
2. `AppDefinitionInspector` finds the `[AppManifest]` classes (by attribute
   name), applies the rules, instantiates each class **twice** and
   compares the canonical JSON;
3. if there are **no** errors, `ManifestWriter` writes the files (otherwise nothing is
   written); a file is rewritten only if its bytes change;
4. the stamp file `obj/<config>/<tfm>/hackeros-app-manifests.txt` lists the
   manifests produced.

### Files produced

- one `[AppManifest]` class → `manifest.json` at the project root;
- several classes → `manifests/<appId>.json`; switching from one form to the other
  deletes the files of the old form.

The JSON is the canonical JSON of ADR 0010 (`AppManifestJsonSerializer.SerializeCanonical`),
schema version 1, with no added field. The files are **tracked by git** (a
capability change shows up in the PR) and are **not** MSBuild content
(`Content Remove`): they are never copied or published. A manual edit is
overwritten at the next build.

### MSBuild integration

`build/HackerOs.Apps.targets`, imported by `Directory.Build.targets`:

| Element | Role |
| --- | --- |
| `$(HackerOsAppProject)` | `true` by default under `Apps/`; a project elsewhere sets it itself. There is no exception: a project under `Apps/` without an `[AppManifest]` class fails with `HOSAPP0001`. |
| `ProjectReference` to the generator | Added automatically to app projects for build ordering (`ReferenceOutputAssembly=false`, `PrivateAssets=all`: the tool never enters the graph of a WebAssembly host). |
| Target `GenerateHackerOsAppManifests` | After `CopyFilesToOutputDirectory`. Incremental: inputs = compiled assembly, tool, dependency list, existing manifests; output = stamp file. |
| Target `GetHackerOsAppManifests` | Query target for the host: the project's manifests, with the metadata `AssemblyName`, `AssemblyFileName` and `AppProject`. Empty for a project that is not an app. |

### Error codes

Each error is an MSBuild error in canonical format, attributed to the `.csproj`,
with the full name of the class at fault: `App.csproj : error HOSAPP0005: [Ns.MyApp] ...`.

| Code | Rule |
| --- | --- |
| `HOSAPP0001` | No `[AppManifest]` class in the app project. |
| `HOSAPP0002` | Non-public class. |
| `HOSAPP0003` | Abstract class. |
| `HOSAPP0004` | Does not derive from `AppDefinition`. |
| `HOSAPP0005` | No public parameterless constructor. |
| `HOSAPP0006` | The constructor or a getter throws an exception. |
| `HOSAPP0007` | `EntryPoint`/`MobileEntryPoint` null or in another assembly. |
| `HOSAPP0008` | Non-deterministic getters (two instances, two manifests). |
| `HOSAPP0009` | `AppManifestValidator` refuses the manifest (the validator's code is repeated). |
| `HOSAPP0010` | `AppEntryPointDiscovery` rules: concrete type, base class according to the kind, `IAppBackHandler` if `SupportsBack`; checked for each declared platform. |
| `HOSAPP0011` | Two classes of the same assembly declare the same identifier. |
| `HOSAPP0020` | Aggregation: manifest file missing or non-strict JSON. |
| `HOSAPP0021` | Aggregation: invalid catalog (duplicate identifier, missing or cyclic dependency) or an app that the host's SDK would refuse (`catalog.sdk.incompatible`, ADR 0098 §3). |
| `HOSAPP0022` | Aggregation: command name or alias declared by two apps of the host (case-insensitive comparison), or a name that the shell executes itself (`ShellInterpreter.ReservedNames`) (ADR 0098 §4). |
| `HOSAPP0023` | Generation: resource declared in `Assets` missing from the project's `wwwroot/`; its SHA-256 cannot be computed (ADR 0098 §5). |
| `HOSAPP0024` | Aggregation: non-system app that declares a negative `OomScoreAdjust` (ADR 0126 §4, ADR 0142 §6, [`oom-killer.md`](../dev/oom-killer.md)). |
| `HOSAPP0025` | Removed, never reused: refused `hackeros.file-view` and `hackeros.file-dialogs` to a non-system app (ADR 0153 §4), which ADR 0156 opens to any windowed app (ADR 0158 §1). |
| `HOSAPP0030` | SDK boundary: `ProjectReference` forbidden in an app project (ADR 0055 §2, number fixed by ADR 0059). Emitted by the target `ValidateHackerOsSdkBoundary`. See [`app-sdk.md`](app-sdk.md). |
| `HOSAPP0031` | SDK boundary: injected service that is neither an SDK type, nor MudBlazor, nor `IJSRuntime` (ADR 0055 §3, ADR 0059). See [`app-sdk.md`](app-sdk.md). |
| `HOSAPP0040` | Manifest translations (ADR 0104 §4): unreadable `Resources/AppManifest.<culture>.resx`, invalid culture name, key other than `Name`, `Description`, `Terminal.Usage` (prefixed with the id in a multi-app project), `Terminal.Usage` for an app that is not a command, or a referenced `localization/` file that is unreadable at aggregation. See [`localization.md`](localization.md). |
| `HOSAPP0090` | Invalid tool arguments. |
| `HOSAPP0091` | App assembly cannot be loaded. |
| `HOSAPP0099` | The tool failed without a diagnostic (crash). |

Dependencies between apps are not checked at generation (only one app
is known): they are checked at aggregation in the host.

### Migrating the apps

The 44 projects under `Apps/` (12 apps, 28 commands, 4 samples) have their
`[AppManifest]` class and their generated `manifest.json`; there is no hand-written
`app.manifest.json` left and no `StaticManifest` field.
`Apps/System/HackerOs.Apps.Calculator` (`CalculatorApp`) served as the example.

Class naming convention: name of the entry point without the `Window` or
`Command` suffix, followed by `App` (`CalculatorWindow` → `CalculatorApp`,
`RmCommand` → `RmApp`). The samples, whose entry point already carries this kind
of name, use `<Project>SampleApp` (`TerminalSampleApp`, `WindowSampleApp`,
`ServiceSampleApp`, `PlatformSampleApp`). The class is in the namespace of its
entry point.

The values come from the old `app.manifest.json`, which was authoritative at runtime:
when a C# `StaticManifest` diverged (resource profile `None`, shortened
description), it is the JSON that was taken over. The code and tests that read
`StaticManifest` use `AppDefinitionMapper.ToManifest(new XxxApp())`; this is
also the case for the parameterless constructors of `SampleTerminalApp` and
`SampleTickerService`.

Verification: a temporary migration test
(`LegacyManifestMigrationTests`, commit `feat(app-model): [AppManifest] classes
for the 43 remaining apps`) compared, for the 43 apps, the generated `manifest.json`
and the old file after strict reading, normalization of the optional
collections and canonical serialization: 43 equal. It was deleted along with the
old files. The only discrepancy fixed before comparison: the `app.manifest.json`
of the TerminalApp sample used the out-of-schema keys `commandName` and
`helpSummary` (refused by strict reading); they are `name` and `usage`, already
used by its `StaticManifest`.

### Built-in commands: a single project (ADR 0051)

Since work item `rebuild/3-commands-merge`, the built-in commands (28, then
29 with `trash` of work item `rebuild/3-trash`, see [`trash.md`](../dev/trash.md)) are
in a single project, `Apps/Commands/HackerOs.Commands/HackerOs.Commands.csproj`:

- one folder per command (`Cat/CatApp.cs`, `Cat/CatCommand.cs`), each keeping
  its namespace (`HackerOs.Commands.Cat`) and its `[AppManifest]` class;
- the generator writes `manifests/<appId>.json` ("several classes" form);
  only `entryPoint.assembly` changes, it is `HackerOs.Commands.dll`;
- **app identifiers do not change** (`org.hackeros.cmd.cat`,
  `org.hackeros.commands.curl`…): `apps.config`, `app-permissions.config` and
  existing aliases remain valid;
- the host has only one `ProjectReference ... IsSystemApp="true"` for all
  commands; the assembly is loaded on demand as a single block
  (`BuildKnownLazyAssemblies.Names` contains it only once);
- the test projects (`HackerOs.Commands.Tests`, `HackerOs.Commands.Wave5.Tests`,
  `HackerOs.Commands.Nano.Tests`, `HackerOs.Network.Tests`) reference this project.

`cd`, `pwd`, `clear`, `help` and `sudo` are installed and resolved like the
other commands: the terminal knows no names (work item
`rebuild/3-shell-commands`, ADR 0051 sections 2 to 5, see
[`shell-commands.md`](../dev/shell-commands.md)). Those that act on the shell
declare the `shell.control` capability. A third-party command remains a separate project (template:
`Apps/Samples/HackerOs.Samples.TerminalApp`).

### Tests

`Tests/HackerOs.Tools.ManifestGenerator.Tests`:

- `AppDefinitionInspectorTests`: one test per code `HOSAPP0001` to `HOSAPP0011`
  on test definitions;
- `ManifestWriterTests`: one/several file forms, no rewrite if
  identical, overwriting of a manual edit;
- `MsBuildIntegrationTests`: real build of the out-of-solution projects
  `Tests/ManifestGenerator.Fixtures.SingleApp`, `.MultiApp` and `.NoDefinition`
  (`HOSAPP0001`).

### Manifest translations (ADR 0104 §4)

After inspection, `generate` reads `Resources/AppManifest.<culture>.resx`
(`ManifestLocalizationGenerator`): keys `Name`, `Description`,
`Terminal.Usage`, prefixed with the app id in a multi-app project
(`org.hackeros.commands.ls.Description`). A missing key keeps the
English text. It writes `localization/<culture>.json` (one app) or
`localization/<appId>/<culture>.json` (several apps), in the format
`{"name","description","terminalUsage"?}`, adds the entry
`localizations: [{"culture":"fr","resourcePath":"localization/fr.json"}]` and
deletes stale files. These `.resx` files are not compiled into the
assembly; the generated files are committed like `manifest.json` and are
not declared as assets. Error: `HOSAPP0040`. See
[`localization.md`](localization.md).

## Aggregation in the host: `BuiltInApplications.json`

### Declaring a host

A host project declares `<HackerOsAppHost>true</HackerOsAppHost>`. In this repository,
only `OS/HackerOs.Ecosystem` does so. `test/test` reuses the compiled
catalog of the Ecosystem (`BuildKnownLazyApps.Catalog`) and does not aggregate
itself; the server serves the Ecosystem's published files as they are, apps
included (ADR 0130). Outside the repository, a host references the package
`HackerOs.App.Build` and declares the same property (`hackeros-host` template, see
[`sdk-packages.md`](sdk-packages.md)).

### Target `CollectHackerOsBuiltInApplications`

In `build/HackerOs.Apps.targets`, after `ResolveProjectReferences` (so before
resource preparation and before the Blazor targets):

1. for each direct `ProjectReference` (excluding references with
   `ReferenceOutputAssembly=false`), calls `GetHackerOsAppManifests` with the
   same global properties as `ResolveProjectReferences`; a project that is
   not an app returns nothing and is ignored;
2. attaches to each manifest the `IsSystemApp` metadata of its `ProjectReference`
   (empty = `false`, meaning fixed by ADR 0048);
3. calls `ManifestGenerator aggregate`, which rereads each manifest in strict mode,
   validates it, builds the catalog with `AppCatalog.Build` (duplicate identifier,
   missing or cyclic dependency = `HOSAPP0021`) and writes
   `obj/<config>/<tfm>/BuiltInApplications.json` only if its bytes change
   (no needless recompilation); it also measures there each app's
   **image** (ADR 0068) from the path of its assembly and its list of
   dependencies, which `GetHackerOsAppManifests` returns (`AssemblyPath`,
   `DependenciesFile`); finally it copies the translated texts that
   `localizations` references into the optional `localized` property of each entry
   (`{"fr":{"name":…}}`, ADR 0104), which `AppCatalog.Localize` applies;
4. adds this file as an `EmbeddedResource` (`HackerOs.BuiltInApplications.json`);
5. adds a `BlazorWebAssemblyLazyLoad` for each app's assembly (first
   removing any hand-written duplicate);
6. passes to ILLink the file `obj/<config>/<tfm>/HackerOs.TrimmerRoots.xml` that
   the same command writes (`--trimmer-roots`): each assembly named by a
   manifest is kept whole there for a trimmed publish (ADR 0106,
   ADR 0113, [`trimmed-publish.md`](../dev/trimmed-publish.md)).

Apps installed as a **package** (ADR 0098 §2) follow the same path: each
app package contains its manifests under `hackeros/manifests/` (their translations
under `hackeros/localization/`) and a file
`buildTransitive/<PackageId>.targets` that declares them as `HackerOsPackagedApp`
items (metadata `PackageId`, `AssemblyFileName`,
`AssemblyPath`). The target `_HackerOsCollectPackagedAppManifests` adds them to
the list; `IsSystemApp` comes from the host's `PackageReference` on that package
(an app package that arrives as a dependency is never system). The target
`_HackerOsPackageAppManifests` produces these files when an app project is
packaged.

The anchoring after `ResolveProjectReferences` is checked by a
`dotnet publish -c Release` of the Ecosystem: `HackerOs.Apps.Calculator` appears
in the `lazyAssembly` section of the published boot configuration, and the
generator does not appear in `_framework`.

### Format

```json
{"schemaVersion":1,"applications":[{"isSystemApp":false,"manifest":{"schemaVersion":1,"id":"org.hackeros.calculator", "...": "..."},"image":{"assemblies":[{"name":"HackerOs.Apps.Calculator.dll","bytes":17408,"shared":false},{"name":"HackerOs.AppSdk.dll","bytes":400896,"shared":true}]}}]}
```

`image` (optional, ADR 0068) lists the assemblies the app maps, sorted by
name, with their size: its own (`shared: false`, `VmExe`) and the
libraries it references, directly or not (`shared: true`), excluding the
.NET runtime and framework (`System.*`, `Microsoft.*`). The computation is done by
`AppImageInspector` (metadata only, no code loaded); see
[`memory-accounting.md`](../dev/memory-accounting.md). The versioned `manifest.json`
does not change: sizes live only in this file produced at each
build.

`BuiltInApplicationsJsonSerializer` (`HackerOs.App.Abstractions`, next to
`AppManifestJsonSerializer`) writes the canonical form (applications sorted by
identifier, canonical manifests) and rereads in strict mode (unknown or
duplicate property, `schemaVersion` other than 1, non-boolean `isSystemApp` = error;
each manifest goes through `AppManifestJsonSerializer.DeserializeStrict`). The
type `BuiltInApplication(IsSystemApp, Manifest, Image)` carries an entry
(`Image` is `null` when the field is absent). The file
is never written by hand or versioned.

### Reading at startup

`HostAppCatalog.Load(assembly)` (`HackerOs.Ecosystem.Host`) reads the resource
`HackerOs.BuiltInApplications.json` of the host's assembly and builds
`Catalog` with `AppCatalog.BuildFromBuiltInApplications`: the catalog carries
the system flags (see "System apps" below); `Images` gives each
app's image (read by memory measurement) and `LazyAssemblyNames` the only
assemblies that the lazy loader can fetch. In the Ecosystem,
`BuildKnownLazyApps` (`Catalog`, `Images`, `Host`) and
`BuildKnownLazyAssemblies.Names` are simple facades over its own
assembly. Only the
JSON is read: the app DLLs stay loaded at first launch and the
`AppDefinition` classes are never instantiated at runtime.

It is the only source: `HackerOs.Ecosystem.csproj` no longer embeds a hand-written
manifest (`HackerOs.Ecosystem.Manifests.*`) and no longer has a
`BlazorWebAssemblyLazyLoad` list, and the transition target
`_HackerOsMarkLegacySystemManifests` disappeared with them: `IsSystemApp` comes
only from `BuiltInApplications.json`. A missing resource is a startup error.

### Installing an app

Once the app has its `[AppManifest]` class: a `ProjectReference` in
`HackerOs.Ecosystem.csproj`, with `IsSystemApp="true"` if the host declares it
system. Nothing else.

### Tests

- `BuiltInApplicationsJsonSerializerTests` (`HackerOs.App.Abstractions.Tests`):
  sorting and canonical round trip, empty document, normalization, strict rejections.
- `BuiltInApplicationsAggregatorTests`: `IsSystemApp` carried over, duplicate
  identifier refused (`HOSAPP0021`), missing or invalid manifest (`HOSAPP0020`),
  list parsing.
- `MsBuildIntegrationTests.Host_embeds_BuiltInApplications_...`: real build of
  `Tests/ManifestGenerator.Fixtures.Host` (a system app, a two-app project,
  a project that is not an app, ignored).
- `BuildKnownLazyAppsTests` (`HackerOs.Ecosystem.Tests`): the catalog contains
  all the host's apps (75 today: 12 windowed and 63 commands, `stress` since ADR 0070); all come from the aggregated resource (no
  `HackerOs.Ecosystem.Manifests.*` resource), and the lazy loading list is
  exactly that of their assemblies.

## SDK version, commands, integrity (ADR 0098 §3-5)

- **SDK version**: see "Conversion rules". `AppCatalog.Build` sets aside the
  refused apps in `AppCatalog.Rejected` (code `catalog.sdk.incompatible`, and
  `catalog.dependency.unavailable` for an app that depends on one of them as required) instead of
  failing: the OS starts without them, startup writes it to
  `/var/log/syslog` (category `catalog`) and Settings lists them as
  incompatible with the reason (`HackAppInstallState.Incompatible`, `Problem`).
- **Duplicate commands**: refused at build time (`HOSAPP0022`). At load time, if two
  apps nevertheless declare the same name, a system app wins, otherwise the
  first enabled one; the other loses that name (`AppCatalog.FindCommandOwner`,
  `CommandOwners`), a warning `catalog.command.duplicate` goes to
  `/var/log/syslog`, and `ShellCommandResolver` resolves only the owner.
- **Resource integrity**: for each `Assets` entry, the generator computes
  the SHA-256 of the project's `wwwroot/<path>` file and writes it to the manifest
  (`HOSAPP0023` if it is missing); the generator reruns when a file of
  `wwwroot/` changes. At runtime, `AppAssetStore` (Platform.Core) reads a resource
  through its source (`IAppAssetSource`; in the browser
  `StaticWebAssetAppAssetSource`, `_content/<assembly>/<path>`) and checks its
  fingerprint on its **first** read of the session, like `dpkg --verify`. A
  tampered resource is refused (`AppAssetIntegrityException`), the app is marked
  damaged (`HackAppInstallState.Damaged` in Settings) and the event
  `catalog.asset.damaged` goes to `/var/log/syslog`. Assemblies remain
  covered by the integrity of Blazor's boot manifest.

## System apps (ADR 0048 §1)

### Source of trust

System status is declared **only** on the host's `ProjectReference`
(`IsSystemApp="true"`), copied by aggregation into `isSystemApp`. Neither
`AppDefinition`, nor `[AppManifest]`, nor the `manifest.json` schema has a field
for it: a `manifest.json` that contains `isSystemApp` is rejected by the
strict deserializer. An app installed at runtime by the future store will therefore
always be non-system.

### API

`AppCatalog` (`Platform/HackerOs.Platform.Core/AppCatalog.cs`):

| Member | Role |
|---|---|
| `SystemAppIds` | set (ordinal, frozen) of the catalog's system identifiers |
| `IsSystemApp(appId)` | `true` if the host declared the app system |
| `Build(manifests)` | catalog without system apps (tests, empty catalog) |
| `Build(manifests, systemAppIds)` | catalog with the host's system list |
| `BuildFromBuiltInApplications(applications)` | shortcut from the entries of `BuiltInApplications.json` |

A system identifier absent from the manifests makes construction fail
(`catalog.system-app.unknown`): a typo cannot leave an app
unprotected. `BuildKnownLazyApps.Catalog` is the catalog injected by the
hosts (`Program.cs`); OS services therefore read
`AppCatalog.IsSystemApp` from dependency injection.

### System apps of the Ecosystem host

| System | Non-system (uninstallable) |
|---|---|
| Terminal, File Explorer, Settings, Text Editor, Code Editor (since `rebuild/4-sdk-shell-host`: its terminal hosts a shell, ADR 0056 §7), System Monitor, Error Log Viewer, Theme Editor (reads `/etc/hackeros/themes`, ADR 0103), and the 70 commands (project `HackerOs.Commands`, `xdg-open` included since `v3/4`, `xdg-mime` since `v3/4b`, `passwd` since `v3/8`) | Browser, Calculator, Diagnostics, HackPaint, Icon Viewer |

Effects of the status: a system app is not uninstallable (see
[`app-installation.md`](../dev/app-installation.md), ADR 0048 §2-6); its dangerous
capabilities granted by default arrive with ADR 0049.

### Tests

- `AppCatalogTests` (`HackerOs.Platform.Core.Tests`): no system app by
  default, exposed set and ordinal comparison, unknown identifier refused,
  flag taken from each `BuiltInApplication` entry.
- `SystemAppTrustTests` (`HackerOs.App.Abstractions.Tests`): no
  "SystemApp" member on `AppDefinition`, `AppManifest`, `[AppManifest]`; a
  `manifest.json` that claims `isSystemApp` is rejected.
- `BuildKnownLazyAppsTests.System_apps_follow_the_host_ProjectReference_metadata`
  (`HackerOs.Ecosystem.Tests`): the system set of the host's catalog is
  exactly the table above.

## Tasks

- [x] `rebuild/2-app-definition`: `AppDefinition`, `[AppManifest]`,
  `FileHandler`, `TerminalCommand`, `AppDefinitionMapper`, mapper tests
  (`Tests/HackerOs.App.Abstractions.Tests/Definition`).
- [x] `rebuild/2-manifest-generator`: `Tools/HackerOs.Tools.ManifestGenerator` (`generate`, `validate`), `build/HackerOs.Apps.targets`, targets `GenerateHackerOsAppManifests` and `GetHackerOsAppManifests`, `HOSAPP*` errors, test projects, Calculator migrated as an example.
- [x] `rebuild/2-builtin-applications`: target `CollectHackerOsBuiltInApplications`, embedded `BuiltInApplications.json`, automatic lazy load, `BuildKnownLazyApps` reads the new file, `IsSystemApp` carried over.
- [x] `rebuild/2-system-apps` (ADR 0048): `AppCatalog.SystemAppIds` and `IsSystemApp`, `AppCatalog.Build(manifests, systemAppIds)` and `BuildFromBuiltInApplications`, `IsSystemApp="true"` set on the Ecosystem's `ProjectReference`s, transition bridge `_HackerOsMarkLegacySystemManifests`, tests.
- [x] `rebuild/2-app-uninstall` (ADR 0048 §2-6): uninstall through `apps.config`, see [`app-installation.md`](../dev/app-installation.md).
- [x] `rebuild/5-worker-processes` (ADR 0070): `AppManifestAttribute.Execution`, `AppExecutionHost`, `execution` field (schema, whitelist, validator, canonical fixture), defaults by kind in the mapper, each command and service declares its host or gets the default.
- [x] `v3/6-sdk-versioning` (ADR 0098): SDK version written by the generator and checked at load time, `HOSAPP0022`/`HOSAPP0023`, single owner of a command, resource fingerprints and `AppAssetStore`, aggregation of packaged apps, `HostAppCatalog`.
- [x] `v3/4-intents` (ADR 0096): declared intents received by the app, URI schemes `x-scheme-handler/<scheme>`, `xdg-open` command.
- [x] `v3/4b` (ADR 0121): per-user file and scheme associations, `xdg-mime` command.
- [x] `v3/12-localization` (ADR 0104): `AppManifest.<culture>.resx` → `localization/`, `HOSAPP0040`, `localized` property of `BuiltInApplications.json`, `AppCatalog.Localize`.
- [x] `exec/3-oom-killer` (ADR 0126, ADR 0142): `AppDefinition.OomScoreAdjust` (SDK 1.2), `oomScoreAdj` field, `HOSAPP0024`.
- [x] `rebuild/2-app-migration`: `[AppManifest]` classes for the other 43 apps, equality test with the old JSON files, removal of the `app.manifest.json` files, `StaticManifest`, the host's manual lists and the target `_HackerOsMarkLegacySystemManifests`, the `Content Update` rule of `Directory.Build.targets` and the "transition" exceptions.
