> [Version française](../../../../fr/wasm/dev/adr/0047-app-definition-and-generated-manifests.md)

# ADR 0047: C# class manifest (`AppDefinition`) and generated JSON manifests

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 2, App model ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 6.1 and 6.2, decisions D6 to D8).
- **Replaces:** ADR 0010 in part. Replaced are: the hand-written `app.manifest.json` file
  as the canonical source, and the rule "the build fails if the
  canonical file is missing". **Kept** are: the JSON conventions
  (camelCase, rejection of unknown and duplicate properties, `schemaVersion` = 1),
  the validation order, canonical serialization and the principle "no
  authority in the manifest".
- **Complements:** ADR 0044 (the settings schema now comes from the manifest
  class, with no change to the `HackSettings` API).

## Context

An app's manifest currently exists in two copies: a hand-written
`app.manifest.json` and, for some of the apps, a C# copy
`StaticManifest` (commands, examples). Installing an app in the host takes
three lines in `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`: a
`ProjectReference`, a `BlazorWebAssemblyLazyLoad` and an `EmbeddedResource` that
renames the JSON. `BuildKnownLazyApps.cs` then re-reads these resources by name
prefix. Forgetting one of the three lines gives an app that is invisible or impossible to
load, with no build error.

The plan wants a single source, in C#, and a single line in the host (D6 to D8).

## Decision

### 1. `AppDefinition` and `[AppManifest]`

Project `Shared/HackerOs.App.Abstractions` (already referenced by all apps, with no
Blazor dependency), folder `Definition/`, namespace **`HackerOs.App.Abstractions`**.

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AppManifestAttribute : Attribute;

public abstract class AppDefinition
{
    // Required (D6)
    public abstract string Id { get; }            // org.hackeros.text-editor
    public abstract string Name { get; }
    public abstract AppKind Kind { get; }
    public abstract Type EntryPoint { get; }      // desktop entry point

    // Optional, with a default value
    public virtual string Version => "1.0.0";
    public virtual string Description => Name;
    public virtual string PublisherId => "pub.local";
    public virtual string Category => "utilities";
    public virtual AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Visible;
    public virtual IReadOnlyList<string> Icons => [];
    public virtual IReadOnlyList<string> Capabilities => [];
    public virtual IReadOnlyList<FileHandler> FileHandlers => [];
    public virtual TerminalCommand? Terminal => null;
    public virtual Type? MobileEntryPoint => null;
    public virtual bool SingleInstancePerUser => false;
    public virtual bool AutoStart => false;
    public virtual bool SupportsBack => false;
    public virtual AppResourceProfileManifest Resources => AppResourceProfileManifest.None;
    public virtual SettingsSchema? Settings => null;
    public virtual IReadOnlyList<AppDependencyManifest> Dependencies => [];
    public virtual IReadOnlyList<AppIntentDeclarationManifest> Intents => [];
    public virtual IReadOnlyList<TopicPermissionDeclarationManifest> DeclaredTopicPermissions => [];
    public virtual IReadOnlyList<LocalizationManifest> Localizations => [];
    public virtual IReadOnlyList<AssetManifest> Assets => [];
}

public sealed record FileHandler(
    string? MediaType, IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Actions, int Priority = 0);

public sealed record TerminalCommand(
    string Name, string Usage, IReadOnlyList<string>? Aliases = null);
```

- The simple types in common use (`FileHandler`, `TerminalCommand`) have a
  short form. Rare cases reuse the existing `*Manifest` records,
  so as not to duplicate the model.
- **No property deals with authority or system status.** An app cannot
  declare itself a system app (D9, ADR 0048) or grant itself a capability (ADR 0002).
- A non-null `MobileEntryPoint` produces the multi-platform `platform` form of the
  manifest; otherwise, the `entryPoint` form (desktop only), as today.
- `sdkCompatibility.minimumVersion` is not a property: the generator
  writes it from the version of `HackerOs.App.Abstractions` used at
  compile time (`major.minor.0`).
- `AppDefinitionMapper.ToManifest(AppDefinition)` (same folder) converts a
  definition into an `AppManifest`. `EntryPoint` becomes
  `{ "assembly": "<assembly name>.dll", "type": "<full type name>" }`.

**Rules imposed on the class**, checked by the generator:

- `public`, non-abstract, marked `[AppManifest]`, public parameterless
  constructor;
- `EntryPoint` (and `MobileEntryPoint`) in **the same assembly** as the
  definition: lazy loading only loads one DLL per app;
- **pure** getters: no service, clock, randomness or I/O. The
  generator instantiates the class twice and compares the canonical bytes;
  a difference fails the build;
- `AppManifestValidator` passes without errors, as do the entry point
  type rules of `AppEntryPointDiscovery` (a `Window` app points to a
  component, a `Terminal` app to a command, etc.).

An assembly can contain several `[AppManifest]` classes: this is the case of the
single commands project planned in phase 3.

Example:

```csharp
[AppManifest]
public sealed class TextEditorApp : AppDefinition
{
    public override string Id => "org.hackeros.text-editor";
    public override string Name => "Text Editor";
    public override AppKind Kind => AppKind.Window;
    public override Type EntryPoint => typeof(TextEditorWindow);
    public override string PublisherId => "pub.hackeros";
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md", ".log"], ["open", "edit"])];
}
```

### 2. Generating `manifest.json` in each app project (D7)

**Tool:** `Tools/HackerOs.Tools.ManifestGenerator`, which replaces and absorbs
`Tools/HackerOs.Tools.ManifestValidator`. It is a .NET executable that depends on the
`Microsoft.AspNetCore.App` shared framework, so that it can load the Blazor
types of the entry points.

Why an executable and not an in-code MSBuild task or a Roslyn generator:
a Roslyn generator can neither write to the source folder nor run the
getters; an in-code MSBuild task runs under .NET Framework in Visual Studio
and cannot load a `net10.0` assembly. An `Exec` works the same on the
command line and in the IDE.

**MSBuild target:** `GenerateHackerOsAppManifests`, in
`build/HackerOs.Apps.targets`, imported by `Directory.Build.targets`.

- It runs after `CopyFilesToOutputDirectory` when
  `$(HackerOsAppProject)` is `true`. This property is `true` by default for
  any project located under `Apps/`; a project elsewhere sets it itself.
- It calls `ManifestGenerator generate` with the compiled assembly and the list
  `@(ReferencePath)` + `@(ReferenceCopyLocalPaths)`. The tool loads the app in an
  isolated, collectible `AssemblyLoadContext`, which resolves dependencies by name
  from this list, then from the shared framework.
- It is incremental (input: the compiled assembly; output: a marker file
  in `obj/`).

**Files produced**, in canonical JSON (ADR 0010):

- a single `[AppManifest]` class in the project → `manifest.json` at the root of the
  project;
- several classes → `manifests/<appId>.json`, one per app.

The JSON keeps exactly the schema version 1: no "generated" field is
added, since strict readers would reject it. The file is **rewritten on
each build** if its bytes differ. A
manual edit therefore disappears on the next build.

Generated files are **tracked by git**. A change to a capability or
file association thus appears in the PR diff, which helps the
security review. The build does not write the file if its content is
identical, so as not to touch its date.

**Errors:** each rule in section 1 produces an MSBuild error with a stable
code `HOSAPP0001` to `HOSAPP0099` and the full name of the offending class. The
build fails; no partial manifest is written. An app project with no
`[AppManifest]` class is a `HOSAPP0001` error.

**Query target:** `GetHackerOsAppManifests` returns, for an app project,
the list of manifests produced and the name of its assembly. It is used by the host.

### 3. Aggregation in the host: `BuiltInApplications.json` (D7, D8)

A host project declares itself with `<HackerOsAppHost>true</HackerOsAppHost>`. Only
`OS/HackerOs.Ecosystem` does so today. `test/test` reuses the compiled
Ecosystem catalog (`BuildKnownLazyApps.Catalog`) and does not aggregate itself.

Target `CollectHackerOsBuiltInApplications` (same `.targets` file), after
`ResolveProjectReferences`:

1. calls `GetHackerOsAppManifests` on each direct `ProjectReference`; a
   project that is not an app is ignored;
2. reads the `IsSystemApp` metadata on each `ProjectReference` (default `false`,
   meaning set by ADR 0048);
3. calls `ManifestGenerator aggregate`, which re-reads and validates each manifest,
   builds the catalog with `AppCatalog.Build` (duplicate identifiers,
   missing or cyclic dependencies = build error) and writes
   `$(IntermediateOutputPath)BuiltInApplications.json`;
4. adds this file as an `EmbeddedResource` with the logical name
   `HackerOs.BuiltInApplications.json`;
5. adds a `BlazorWebAssemblyLazyLoad` for each app's assembly, before the
   Blazor targets that consume this item. The right anchor target is verified
   by a `dotnet publish` of the Ecosystem in the work item that implements it.

**Format**, canonical, applications sorted by identifier:

```json
{"schemaVersion":1,"applications":[{"isSystemApp":true,"manifest":{"schemaVersion":1,"id":"org.hackeros.text-editor", "...": "..."}}]}
```

`BuiltInApplications.json` is never written by hand or versioned: it is
produced in `obj/` on every host build. The format will also serve the future
app store (an app installed at runtime will always have `isSystemApp: false`).

### 4. A single line in the host (D8)

Installing an app = one `ProjectReference` in `HackerOs.Ecosystem.csproj`. The
`BlazorWebAssemblyLazyLoad` and `EmbeddedResource` lists in the `.csproj` are
removed, as is the `Content Update="app.manifest.json"` rule in
`Directory.Build.targets`.

### 5. Reading at startup

`BuildKnownLazyApps` reads the `HackerOs.BuiltInApplications.json` resource with a
strict deserializer (`BuiltInApplicationsJsonSerializer`, next to
`AppManifestJsonSerializer`) instead of the `HackerOs.Ecosystem.Manifests.*` resources.
It exposes the catalog as today, plus the `IsSystemApp` information per
app (ADR 0048). App DLLs are still only loaded on first launch:
`AppDefinition` classes are **never** instantiated at runtime, only the JSON
is read.

### 6. Migration

- Each app under `Apps/` receives an `[AppManifest]` class, with values
  identical to its current `app.manifest.json`. The generated `manifest.json` must
  be semantically equal to the old file (same identifier, capabilities,
  associations, resource profile...). A migration test compares the two
  before the old one is deleted.
- Then `app.manifest.json` and the `StaticManifest` fields are deleted. Commands
  and examples that built their manifest in C# get it through
  `AppDefinitionMapper.ToManifest(new XxxApp())`, including in tests.
- The `HackerOs.Commands.Cd/Pwd/Clear/Help` projects also receive their class,
  but remain unreferenced by the host until phase 3 (ADR 0034).

### 7. Contract tests

- `AppDefinitionMapper`: each optional property has its default value;
  `EntryPoint` and `MobileEntryPoint` produce the right form.
- Generator: one test project per error (`HOSAPP*`); non-deterministic getters
  detected; entry point in another assembly rejected; two classes in a
  project give two files.
- Aggregation: `IsSystemApp` carried over; duplicate identifier rejected; non-app project
  ignored.
- Ecosystem: `BuildKnownLazyApps.Catalog` contains exactly the referenced
  apps, and lazy loading of each app works.

## Consequences

- Adding an app: a project with an `[AppManifest]` class, then one
  `ProjectReference` in the host. No more JSON to write, no more list to maintain.
- Manifest errors are compile errors, with the offending class,
  instead of an app missing at runtime.
- Building an app executes its code (the definition's getters). This is
  acceptable for repository code; for the future store, the published JSON is enough
  and the host runs nothing.
- The JSON schema version 1 and the `AppManifest` runtime format do not change:
  the engine, the catalog and the tests that consume `AppManifest` remain
  valid.
- The `manifest.json` tracked by git can lag behind the code between two
  builds; it is always updated by the next build, and the build
  is authoritative.

## Implementation work items

| Branch | Content | Depends on |
|---|---|---|
| `rebuild/2-app-definition` | `AppDefinition`, `[AppManifest]`, `FileHandler`, `TerminalCommand`, `AppDefinitionMapper`, mapper tests. | n/a |
| `rebuild/2-manifest-generator` | `Tools/HackerOs.Tools.ManifestGenerator` (`generate`, `aggregate`), `build/HackerOs.Apps.targets`, targets `GenerateHackerOsAppManifests` and `GetHackerOsAppManifests`, `HOSAPP*` errors, test projects. | `2-app-definition` |
| `rebuild/2-builtin-applications` | Target `CollectHackerOsBuiltInApplications`, `BuiltInApplications.json`, automatic lazy load, `BuildKnownLazyApps` reads the new file, `IsSystemApp` metadata carried over. | `2-manifest-generator` |
| `rebuild/2-app-migration` | `[AppManifest]` classes for the 44 app projects (12 apps, 28 commands, 4 examples), equality test against the old JSON, deletion of `app.manifest.json`, `StaticManifest` and the host's manual lists. | `2-builtin-applications` |

## References

- ADR 0002 (authority), ADR 0010 (partially replaced), ADR 0034 (commands not
  wired), ADR 0044 (settings schema), ADR 0048 (system apps).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 6.1 and 6.2.
- `OS/HackerOs.Ecosystem/BuildKnownLazyApps.cs`, `HackerOs.Ecosystem.csproj`.
