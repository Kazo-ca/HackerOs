# HackerOS App SDK 1.0 Developer Guide

## Overview

The HackerOS App SDK provides a C# and Blazor WebAssembly framework for building desktop-class applications, command-line utilities, and session background services for the HackerOS computer simulation environment.

---

## Architecture & Project Structure

HackerOS applications must be created in dedicated assemblies referencing only the public App SDK packages:

| Package / Project | Target Application Kind | Key Classes & Features |
|---|---|---|
| `HackerOs.App.Abstractions` | All Applications | Manifests, Capabilities, Authorities, `VirtualPath`, Result/Error types |
| `HackerOs.AppSdk` | Terminal & Service Apps | `AppBase`, `TerminalAppBase`, `ServiceAppBase`, `TerminalExecutionContext` |
| `HackerOs.AppSdk.Blazor` | Window Applications | `WindowAppBase`, `IFileDialogService`, Scoped CSS support, MudBlazor wrappers |
| `HackerOs.AppSdk.Icons` | Any Application, or the OS shell | `IIconCatalog`, `HackerIcon`, five icon libraries as themeable inline SVG |

---

## App Kinds & Entry Points

Every application defines an `app.manifest.json` declaring its application kind:

### 1. Window App (`kind: "window"`)
Visual Blazor component inheriting `WindowAppBase`.
```razor
@using HackerOs.AppSdk.Blazor
@inherits WindowAppBase

<div class="my-app">
    <h2>My HackerOS Window App</h2>
    <button @onclick="OpenSampleFile">Open File</button>
</div>

@code {
    private async Task OpenSampleFile()
    {
        var result = await base.OpenFileAsync(new OpenFileDialogRequest { Title = "Select File" });
    }
}
```

### 2. Terminal App (`kind: "terminal"`)
Command-line tool deriving `TerminalAppBase`.
```csharp
using HackerOs.AppSdk;

public class MyCommandApp : TerminalAppBase
{
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        await context.StandardOutput.WriteLineAsync("Hello from custom command!".AsMemory(), cancellationToken);
        return 0;
    }
}
```

### 3. Service App (`kind: "service"`)
Background daemon deriving `ServiceAppBase`.
```csharp
using HackerOs.AppSdk;

public class MyServiceApp : ServiceAppBase
{
    protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        while (!sessionCancellationToken.IsCancellationRequested)
        {
            await Task.Delay(5000, sessionCancellationToken);
        }
    }
}
```

---

## Manifest Format (`app.manifest.json`)

```json
{
  "schemaVersion": 1,
  "id": "com.example.myapp",
  "name": "My App",
  "version": "1.0.0",
  "publisherId": "pub.example",
  "description": "Custom HackerOS Application",
  "kind": "window",
  "entryPoint": {
    "assembly": "MyApp.dll",
    "type": "MyApp.MainComponent"
  },
  "sdkCompatibility": {
    "minimumVersion": "1.0.0"
  },
  "capabilities": [
    "filesystem.user-home.read",
    "filesystem.user-home.write",
    "dialogs.file-open"
  ]
}
```

---

## Icons

Any app (window, terminal-adjacent tooling, or the OS shell) can draw a themeable
inline-SVG icon from Bootstrap Icons, Font Awesome, Lucide, or Simple Icons by
referencing `HackerOs.AppSdk.Icons` and using the `HackerIcon` component:

```razor
@using HackerOs.AppSdk.Icons

<HackerIcon Library="IconLibrary.Bootstrap" Name="house" Size="20" />
```

Material Design icons don't need this package at all — use MudBlazor's own bundled
`Icons.Material.Filled.*` constants directly in any app that already references
MudBlazor. See [`../icon-library.md`](../icon-library.md) for the full guide
(searching via `IIconCatalog`, licensing/attribution, and how to regenerate the
bundled icon data), and the "Icon Viewer" app (`docs/apps/icon-viewer.md`) to browse
every available icon interactively.

---

## Capability Policy & Enforcement

Applications request exact capability identifiers in `app.manifest.json`. The OS grants capabilities based on user security policy:
- Common capabilities: `filesystem.user-home.read`, `filesystem.user-home.write`, `dialogs.file-open`, `dialogs.file-save`, `apps.launch`, `settings.read`, `settings.write`.
- An operation requiring ungranted capabilities returns `FileSystemErrorCode.CapabilityDenied`.

---

## Validation Tooling

Validate app manifests using the CLI validator:
```bash
dotnet run --project Tools/HackerOs.Tools.ManifestValidator -- app.manifest.json
```
