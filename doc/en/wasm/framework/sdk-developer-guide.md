# HackerOS app developer guide

## Purpose

This guide explains how to write an app for HackerOS with the public SDK:
a window, a shell command or a service. It starts from zero and goes
through to installation and testing. The complete list of APIs is in the
[SDK reference](sdk-reference.md); the rules of the SDK boundary are
in [`app-sdk.md`](app-sdk.md).

All the examples in this guide were compiled and the test in the last
section was run against the code of `rebuild/base` (2026-10-11).

## The three kinds of app

| Kind (`AppKind`) | Entry point | Examples in the OS |
|---|---|---|
| `Window` | Razor component derived from `WindowAppBase` | Text Editor, File Explorer, Calculator |
| `Terminal` | class derived from `TerminalAppBase`, launched by the shell | `ls`, `cat`, `grep`, `nano` |
| `Service` | class derived from `ServiceAppBase`, without an interface, bound to the session | services started at sign-in |
| `Web` | class derived from `WebServerAppBase`, URL in `Urls` (`web.urls`), started at the first request in the shared `web` worker; with ASP.Hack, a `WebAppDefinition` | internal `.hack` sites: [ASP.Hack guide](asp-hack-developer-guide.md), Intranet example |

An app is described by an `[AppManifest]` class derived from `AppDefinition`.
The build produces `manifest.json` from it; the OS only reads this file, never the class.

Complete apps serve as models in `Apps/Samples/` (one per kind) and
`Apps/System/` (the base apps, written with the same public SDK).

## 1. Creating the project

An app project lives under `Apps/`: this is what makes it an app project
(`HackerOsAppProject`), with manifest generation and checking of the SDK
boundary.

`Apps/Samples/Example.Notes/Example.Notes.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.13" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\Shared\HackerOs.App.Abstractions\HackerOs.App.Abstractions.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.Simulation.Abstractions\HackerOs.Simulation.Abstractions.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk\HackerOs.AppSdk.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Blazor\HackerOs.AppSdk.Blazor.csproj" />
  </ItemGroup>

</Project>
```

- A command or a service without an interface can use
  `Microsoft.NET.Sdk` and do without `HackerOs.AppSdk.Blazor`.
- Allowed references: the four `Shared/*.Abstractions`, the
  `Shared/HackerOs.AppSdk*` libraries, `Platform/HackerOs.Theming.Blazor` and other
  projects under `Apps/`. Any other project reference fails the build
  (`HOSAPP0030`). `PackageReference`s (MudBlazor for example) are free.
- Add the project to `HackerOs.sln` (`dotnet sln add`).

### From another repository (local packages)

Outside this repository, an app is built on the local packages (ADR 0098 §2,
[`sdk-packages.md`](sdk-packages.md)): `build/pack-local.sh` (or `.ps1`) produces
them in `artifacts/local-nupkg`, a `nuget.config` (template
`build/nuget.config.sample`) points to this folder, then the templates create the
project:

```bash
dotnet new install <HackerOS>/artifacts/local-nupkg/HackerOs.Templates.2.0.3.nupkg
dotnet new hackeros-window -n Contoso.Notes --appId com.contoso.notes
dotnet new hackeros-host -n Contoso.Os          # un hôte qui lance tout l'OS
```

The project references `HackerOs.AppSdk*` and `HackerOs.App.Build`
(`PrivateAssets="all"`), which brings manifest generation, the ASP.Hack route
generator and the boundary rule on packages (`HOSAPP0030`).
A host adds the app through a `ProjectReference` or a `PackageReference`.
The complete guide for a third-party repository (layout, pinned version, host, tests,
publishing, version upgrade, what the SDK 1.0 promise covers) is
[`building-on-hackeros.md`](building-on-hackeros.md).

## 2. Describing the app: `AppDefinition`

```csharp
using HackerOs.App.Abstractions;

namespace Example.Notes;

[AppManifest]
public sealed class NotesApp : AppDefinition
{
    public override string Id => "com.example.notes";
    public override string Name => "Notes";
    public override AppKind Kind => AppKind.Window;
    public override Type EntryPoint => typeof(NotesWindow);
    public override string PublisherId => "pub.example";
    public override string Description => "Prise de notes dans ~/Documents";
    public override string Category => "productivity";

    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.FileSystemUserHomeRead,
        AppCapabilities.FileSystemUserHomeWrite,
        AppCapabilities.FileSystemPrivateRead,    // lire ses paramètres
        AppCapabilities.FileSystemPrivateWrite,   // écrire ses paramètres
        AppCapabilities.DialogFileSave,           // « Enregistrer sous »
        AppCapabilities.NotificationsPost,
    ];

    // Ouvrir un .txt ou un .md depuis l'explorateur ou avec `open` lance cette app.
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md"], ["open", "edit"])];

    // Schéma des paramètres : /etc/com.example.notes/settings.config puis ~/.config/com.example.notes/settings.config.
    public override AppSettingsSchemaManifest? Settings => new(
        SchemaVersion: 1,
        Fields:
        [
            new("font-size", AppSettingValueType.Integer, "14", AppSettingScope.AppUser, AppSettingSensitivity.Public),
            new("theme", AppSettingValueType.Enum, "dark", AppSettingScope.AppUser, AppSettingSensitivity.Public, ["dark", "light"]),
        ]);
}
```

Only `Id`, `Name`, `Kind` and `EntryPoint` are required; the other
members have a default value (full table in
[`app-model.md`](app-model.md#appdefinition-et-appmanifest)). The most
useful members:

| Member | Role |
|---|---|
| `Capabilities` | Rights requested, taken from `AppCapabilities` ([reference](sdk-reference.md#capacités)). Request only what is needed: a dangerous capability requires administrator approval at first launch. |
| `FileHandlers` | File types the app knows how to open; the path arrives in `LaunchFile`. |
| `Terminal` | For a command: name, usage and aliases (`new("wc-notes", "wc-notes [-l] FILE...", ["wcn"])`). |
| `Settings` | Schema of the settings read by `HackSettings`. |
| `SingleInstancePerUser` | A single window; a second launch activates it. |
| `AutoStart` | For a service: started at sign-in. |
| `LaunchVisibility` | `Hidden` to not appear in the Start menu (commands, services). |
| `Icons` | Images relative to `wwwroot/`, also declared in `Assets`; the first appears in the title bar and the taskbar (ADR 0164). macOS hides the title one. The build computes the asset's fingerprint. |
| `Category`, `Version` | Presentation and classification of the app. |
| `OomScoreAdjust` | Starting `oom_score_adj` (SDK 1.2); a negative value, which protects from the OOM killer, is reserved for system apps (`HOSAPP0024`, [`oom-killer.md`](../dev/oom-killer.md)). |

Rules enforced by the generator: public, non-abstract class, with a
parameterless constructor; pure getters (the generator instantiates the class
twice and compares); entry point in the same assembly. A project can
contain several `[AppManifest]` classes: the build then writes
`manifests/<appId>.json` instead of `manifest.json`.

The generated file is **tracked by git**: a capability change shows up
in review. Never edit it by hand, the build overwrites it.

## 3. A windowed app

The entry point is a Razor component that inherits from `WindowAppBase`. CSS and
JavaScript go in files next to the component, never inline (the
build refuses `style="..."`, `<style>`, `<script>` and `onclick="..."`).

`NotesWindow.razor`:

```razor
@inherits WindowAppBase
@using HackerOs.AppSdk.Blazor

<div class="notes">
    <header class="notes-toolbar">
        <span class="notes-path">@_path</span>
        <button class="notes-button" @onclick="SaveAsync">Enregistrer</button>
        <button class="notes-button" @onclick="SaveAsAsync">Enregistrer sous…</button>
    </header>
    <textarea class="notes-text notes-size-@_fontSize" @bind="_text"></textarea>
    <footer class="notes-status">@_status</footer>
</div>
```

`NotesWindow.razor.cs`:

```csharp
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Notifications;

namespace Example.Notes;

public partial class NotesWindow
{
    private string _path = "";
    private string _text = "";
    private string _status = "";
    private int _fontSize = 14;

    protected override async Task OnAppInitializedAsync()
    {
        // Ouverte depuis l'explorateur ou par `open notes.md` : le fichier est le premier argument.
        _path = LaunchFile?.Value
            ?? HackPath.Combine(HackEnvironment.GetFolderPath(HackSpecialFolder.MyDocuments), "notes.txt");

        if (await HackFile.ExistsAsync(_path))
        {
            _text = await HackFile.ReadAllTextAsync(_path);
        }

        _fontSize = await HackSettings.GetAsync<int>("font-size");
    }

    private async Task SaveAsync()
    {
        try
        {
            await HackFile.WriteAllTextAsync(_path, _text);
            AppContext.Logging.Log(DiagnosticSeverity.Information, $"Saved {_path}");   // /var/log/apps/com.example.notes.log
            _status = "Enregistré.";
        }
        catch (HackUnauthorizedAccessException ex)
        {
            _status = $"Accès refusé ({ex.RequiredCapability ?? "droits du fichier"}).";
        }
        catch (HackIOException ex)
        {
            _status = $"Erreur : {ex.Message}";
        }
    }

    private async Task SaveAsAsync()
    {
        SaveFileDialogResult result = await SaveFileAsync(new SaveFileDialogRequest
        {
            Title = "Enregistrer la note",
            SuggestedFileName = HackPath.GetFileName(_path),
            DefaultExtension = ".txt",
        });

        if (result.Status == FileDialogStatus.Selected && result.Resource is not null)
        {
            _path = result.Resource.Path.Value;
            await SaveAsync();
            AppContext.Notifications.Post(NotificationSeverity.Success, "Notes", $"Note enregistrée dans {_path}");
        }
    }
}
```

`NotesWindow.razor.css` uses the theme tokens (`--hos-*`), to follow
the theme chosen by the user:

```css
.notes {
    display: flex;
    flex-direction: column;
    height: 100%;
    background: var(--hos-bg-base);
    color: var(--hos-text-primary);
    font-family: var(--hos-font-ui);
}

.notes-text {
    flex: 1;
    font-family: var(--hos-font-mono);
    background: var(--hos-input-bg);
    color: var(--hos-input-color);
    border: 1px solid var(--hos-border);
}
```

### What `WindowAppBase` provides

| Member | Role |
|---|---|
| `AppContext` | Execution context of the instance (`IAppExecutionContext`): gateways, identity, granted capabilities. |
| `LaunchArguments`, `LaunchFile` | Process arguments without `argv[0]`; first argument if it is an absolute path. |
| `OnAppInitialized[Async]`, `OnAppParametersSet[Async]`, `OnAppAfterRender[Async]`, `ShouldRenderApp` | Life cycle. The Blazor methods (`OnInitializedAsync`...) are sealed: override these instead. |
| `OpenFileAsync`, `SaveFileAsync`, `SelectFolderAsync` | OS file dialogs (capabilities `dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select`); the result carries the chosen path (`Resource.Path`, `Resources`). |
| `MessageBox`, `TextInput` | Message box and text input. |
| `WindowTitle`, `SetWindowTitleAsync`, `FormatWindowTitle` | Window title (ADR 0117, SDK 1.1): the app writes the whole title, by convention `document — App` with `*` in front if the document is modified; `null` gives back the app name. See below. |
| `OnPermissionErrorRaised` | Override to display a permission error yourself (set `ErrorHandled = true`) instead of the OS notification. |
| `OnAppStopRequestedAsync(e)` | The OS requests a stop (`kill`, "End task", sign-out; ADR 0069): finish or cancel the operation in progress, write a draft of unsaved changes (`HackRecoveryDraft`). The window closes when the returned task completes, at the latest at `e.Deadline`. |

The component can also implement:

- `IWindowCloseGuard` (`ConfirmCloseAsync`) to ask for confirmation before
  closing, for example if unsaved changes remain
  (CodeEditor does this);
- `IAppBackHandler` to handle the Back button (mobile and title bar);
  then declare `SupportsBack => true`.

### Window events and size

Override `OnWindowActivated[Async]`, `OnWindowDeactivated[Async]`,
`OnWindowStateChanged[Async](previous, current)` and `OnWindowResized[Async](size)`
to react to focus, minimizing, maximizing, restoring and the
end of a resize; `IsWindowActive`, `WindowState` and `WindowSize`
give the last state. An exception in a hook is written to
`/var/log/apps/<appId>.log` without blocking the window.

The window size is declared in the definition:

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 720, DefaultHeight = 520, MinWidth = 480, MinHeight = 360,
};
```

The title is set from the app (SDK 1.1, ADR 0117). Setting it in
`OnAppInitialized[Async]` displays it with the window; setting it again after each
render keeps it up to date (asking for the same title again does nothing):

```csharp
protected override Task OnAppAfterRenderAsync(bool firstRender) =>
    SetWindowTitleAsync(FormatWindowTitle(_fileName, _isDirty)).AsTask(); // « *notes.txt — Mon app »
```

Without a declaration, the window is 800×560 with a minimum of 360×240.
`Resizable = false` removes the handles and the Maximize button. The OS remembers the
size and maximized state of the window from one opening to the next, not its
position (ADR 0122); `RememberGeometry = false` (SDK 1.1) makes it always
open at its default size ([`window-geometry.md`](../dev/window-geometry.md)). Details, hook
order and the window manager's keyboard shortcuts:
[`window-model.md`](window-model.md).

### Launched from the terminal

Typing the last segment of the app's identifier in the terminal launches it
(ADR 0064): `my-app --verbose notes.txt` for `com.example.my-app`. The words
that follow arrive in `LaunchArguments` (relative to the shell's folder:
`HackPath.GetFullPath` resolves them), and `HackConsole` writes to that terminal,
like `printf` of a Linux graphical program:

```csharp
protected override void OnAppInitialized()
{
    if (HackConsole.IsAttached)
    {
        HackConsole.WriteLine($"my-app: opening {string.Join(' ', LaunchArguments)}");
    }
}
```

The shell waits for the window to close (`my-app &` gives the prompt back);
launched from the desktop, the app writes to `/dev/null`. See
[`shell-interpreter.md`](../dev/shell-interpreter.md#apps-lancées-par-leur-nom).

### Ambient context and child components

The `Hack*` facades know which app is calling them thanks to an ambient context
that the OS opens around the life cycle, the event handlers
(`@onclick`...) and the rendering of `WindowAppBase`. **Every child component**
inherits from `HackComponentBase`: it receives the same context as a cascading value. Since
phase 5, this is true even if it does not call `Hack*`: the time of code run
outside a context is charged to the compositor, not to the app (ADR 0067), and a test
(`AmbientFrameSourceTests`) checks it for the apps in `Apps/`. A
MudBlazor dialog, rendered outside the window, receives the window's context as a
parameter and adopts it before the life cycle of `HackComponentBase` (as
`FileExplorerDialogBase` did, removed by ADR 0155: the OS file dialogs
now go through `IDialogService`). A `[JSInvokable]` method or a
.NET timer callback (outside `AppContext.Clock`) opens the context itself,
from its first statement:

```csharp
using (HackExecutionContext.Enter(AppContext))
{
    await HackFile.WriteAllTextAsync("~/autosave.txt", _text);
}
```

Without a context, the API throws `HackExecutionContextMissingException`; it never
falls back to a system identity.

### JavaScript

To be used only for a browser API not reachable from C#. The module is
a `MyComponent.razor.js` file next to the component (or under the project's
`wwwroot/`), imported like this and only like this (checked at build, `HOSAPP0031`):

```csharp
[Inject] private IJSRuntime JS { get; set; } = default!;

_module = await JS.InvokeAsync<IJSObjectReference>(
    "import", "./_content/Example.Notes/NotesWindow.razor.js");
```

Never add a `<script>` in a host.

### Do not block the main thread (ADR 0127 §4)

A windowed app runs on the browser's main thread, like the whole
desktop. A long loop without `await` then freezes the whole OS: the mouse, the
other windows and even the "The application is not responding" box, which can only
be displayed after the freeze. Put heavy computation in
`HackBackground.RunAsync` (ADR 0128, it runs in a worker), or call
`HackProcess.CheckpointAsync` often in the loop. When the window stays
busy for more than 5 s, the OS adds "(Not responding)" to it and offers
"Wait" or "End the application" (stop requested then forced after
2 s). See [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md).

## 4. A shell command

A command receives its arguments, its streams and the shell's current directory.
It returns an exit code (0 = success), read by `$?` and `&&`.

```csharp
[AppManifest]
public sealed class WordCountApp : AppDefinition
{
    public override string Id => "com.example.wc-notes";
    public override string Name => "wc-notes";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(WordCountCommand);
    public override string PublisherId => "pub.example";
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("wc-notes", "wc-notes [-l] FILE...", ["wcn"]);
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead];
}

public sealed class WordCountCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        bool lines = context.Arguments.Contains("-l");
        string[] files = [.. context.Arguments.Where(argument => !argument.StartsWith('-'))];
        if (files.Length == 0)
        {
            // Pas de fichier : lire l'entrée standard, pour que `cat a.txt | wc-notes` marche.
            string input = await context.StandardInput.ReadToEndAsync(cancellationToken);
            await context.StandardOutput.WriteLineAsync(Count(input, lines).ToString());
            return 0;
        }

        int exitCode = 0;
        foreach (string file in files)
        {
            try
            {
                // Un chemin relatif part du répertoire courant du shell.
                string text = await HackFile.ReadAllTextAsync(file, cancellationToken);
                await context.StandardOutput.WriteLineAsync($"{Count(text, lines),8} {file}");
            }
            catch (HackFileNotFoundException)
            {
                await context.StandardError.WriteLineAsync($"wc-notes: {file}: No such file or directory");
                exitCode = 1;
            }
        }

        return exitCode;
    }

    private static int Count(string text, bool lines) => lines
        ? text.Split('\n').Length
        : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
```

`TerminalExecutionContext` also gives `Environment` (process
variables), `App` (the app context), `Shell` (`IShellSession`, for a
command that acts on its shell like `cd` or `export`, capability
`shell.control`) and `FullScreen` (full-terminal alternate screen, like
`nano`). Errors go to `StandardError`, messages in the style of the GNU
tools. See [`shell-commands.md`](../dev/shell-commands.md) and
[`shell-interpreter.md`](../dev/shell-interpreter.md).

A long loop (walking a tree, a large file line by line) calls
`await HackProcess.CheckpointAsync(cancellationToken)` on each turn (ADR
0069): it yields control beyond 8 ms, which keeps the OS smooth, and
stops as soon as `kill` requests it. With no stop requested and within its slice,
the call only costs a clock read. A command stopped on its token
exits with `130`; a killed command (`kill -9`, or 5 s timeout exceeded) with
`137`, and its pending continuations no longer run.

## 5. A service

A service runs in the background during the session, without a window. It observes
the cancellation token (sign-out, shutdown, disabling) and can release its
resources in `OnStoppingAsync`.

```csharp
[AppManifest]
public sealed class CleanupApp : AppDefinition
{
    public override string Id => "com.example.tmp-cleanup";
    public override string Name => "Temp Cleanup";
    public override AppKind Kind => AppKind.Service;
    public override Type EntryPoint => typeof(CleanupService);
    public override string PublisherId => "pub.example";
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override bool AutoStart => true;
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
}

public sealed class CleanupService(AppManifest manifest) : ServiceAppBase(manifest)
{
    protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        string tmp = HackPath.GetFullPath("~/tmp");
        try
        {
            while (!sessionCancellationToken.IsCancellationRequested)
            {
                if (await HackDirectory.ExistsAsync(tmp, sessionCancellationToken))
                {
                    foreach (string file in await HackDirectory.GetFilesAsync(tmp))
                    {
                        await HackFile.DeleteAsync(file, sessionCancellationToken);
                    }

                    context.Logging.Log(DiagnosticSeverity.Information, $"Cleaned {tmp}");
                }

                // Le temps simulé de l'OS, pas Task.Delay.
                await context.Clock.DelayAsync(TimeSpan.FromHours(1), sessionCancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Déconnexion ou arrêt : fin normale.
        }
    }
}
```

`OnStoppingAsync(reason, token)` is called only once, as soon as the OS
requests the stop (ADR 0069), before the token is canceled:
`ServiceStopReason.Terminated` for `kill`, "End task" or `systemctl stop`,
`Logout`, `Shutdown` or `Disabled` otherwise. This is where a service that holds
state finishes or cancels its operation in progress.

States and health (ADR 0097, ADR 0107): the service is `activating` as long as
`OnStartAsync(context, token)` (optional: open a port, load a cache)
has not returned, then `active`. A startup of more than 30 s gets it killed as
`failed (Result: timeout)`. An unhandled exception, a non-zero exit code
or a `kill -9` leave it `failed` with the reason, until the next
start or `systemctl reset-failed`. While it runs, it can say
it is unwell without stopping:

```csharp
ReportHealth(HackServiceHealth.Degraded, "database unreachable");
```

Automatic start (`AutoStart => true`, `Automatic` mode): after each
sign-in, under the user; stopped at sign-out. A **system app**
can declare `public override AppServiceStartup? ServiceStartup =>
AppServiceStartup.Boot;`: it then starts at the end of boot, before the sign-in
screen, under the `hackeros-svc` account (home `/var/lib/hackeros-svc`),
continues after sign-out and only stops at shutdown. Another app
that requests `Boot` starts at sign-in, with a warning in
`/var/log/syslog`.

The service appears in the Services tab of the task manager and in
`/proc`. It communicates with other apps through event bus
topics (`context.Events.Publish(topic, payload)` in its own namespace,
ADR 0038): see `Apps/Samples/HackerOs.Samples.ServiceApp`. A
service runs by default in a dedicated Web Worker (ADR 0070): the type of its
payloads must then be declared by `[assembly: HackWireContext(typeof(…))]`
(SDK 1.2, ADR 0124), as the example's `SampleTickerJsonContext` does;
see [`app-sdk.md`](app-sdk.md#processus-dans-des-web-workers-adr-0070-et-0071).

## 6. Using OS services

An app only gets an OS service through the SDK: a **static `Hack*` facade**
(recommended form, it reads the ambient context) or a **gateway**
of `AppContext`. Injecting a platform service is refused at
build (`HOSAPP0031`). Common tasks:

| Need | API | Capability |
|---|---|---|
| Read, write, copy, move files | `HackFile`, `HackDirectory`, `HackPath`, `HackFileInfo` | `filesystem.user-home.*` for `~`, `filesystem.system.*` (dangerous) elsewhere |
| Copy or move like the explorer (name conflicts, "(copy)", cancellation) | `HackFileOperations.CopyAsync`, `MoveAsync`, `ImportAsync`; `IDialogService.AsFileConflictResolver(AppContext)` asks the question (SDK 2.0, ADR 0161 §2) | those of the files |
| Move to the trash | `HackTrash.MoveToTrashAsync` | those of the file |
| Display files in its window (list, icons, tree) | `<HackFileView>` (contract `hackeros.file-view`, provided by the explorer or the app chosen by the administrator, [`embedded-components.md`](embedded-components.md)) | those of the folder |
| Watch a folder | `HackFileSystemWatcher` | those of the folder |
| App settings | `HackSettings.OpenAsync`, `GetAsync`, `SetAsync` | `filesystem.private.read` / `.write` |
| Keep a password or a token | `HackKeyring.SetAsync`, `GetAsync`, or a `SecretReference` key with `HackSettingsDocument.SetSecretAsync` ([`sensitive-settings.md`](../dev/sensitive-settings.md)) | none for its own entries |
| Log | `AppContext.Logging.Log` → `/var/log/apps/<appId>.log` | none |
| Notify the user | `AppContext.Notifications.Post` | `notifications.post` |
| User, special folders, variables, arguments | `HackUser`, `HackEnvironment` | none |
| Launch an app, open a file with its app | `HackApps.LaunchAsync`, `HackApps.OpenFileAsync` | `apps.launch` |
| Open a page in the browser, or in a detached window | `HackBrowser.OpenAsync`, `HackBrowser.OpenDetachedAsync` | `apps.launch` |
| Display part of its window in a detached window | `IDisplaySurfaceService` (injected) and `<DisplaySurfaceContent>` | none |
| Clipboard | `HackClipboard` | `clipboard.read` / `.write` |
| Simulated network (and real if the OS allows it) | `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient`, `HackUdpClient` | `network.simulated.*`, `network.real.access` (dangerous) |
| Processes and services | `HackProcess`, `HackServiceController` | `process.list`, `process.manage`, `services.manage` |
| Read or lower the limits of its process (`ulimit`) | `HackProcess.GetResourceLimitsAsync`, `SetResourceLimitAsync` (SDK 1.2, [`process-limits.md`](../dev/process-limits.md)) | none for its own process; `process.manage` for another |
| Host a shell (terminal, code editor) | `HackShell.StartAsync` | `shell.host` (dangerous) |
| Back up or restore the whole machine (system apps) | `HackSystemBackup.CreateAsync`, `RestoreAsync` | `system.backup` (dangerous) + administrator |
| Simulated time, timers | `AppContext.Clock` | none |
| Take heavy computation out of the window (filter, search, compression) | `HackBackground.RunAsync` (SDK 1.2) | none; the job has the app's capabilities |

Paths accept `~` and relative paths (to the process's current directory).
Errors follow `System.IO`: `HackFileNotFoundException`,
`HackDirectoryNotFoundException`, `HackFileConflictException` (concurrent
revision), `HackIOException`, `HackUnauthorizedAccessException` (with
`RequiredCapability`). A write can fail for lack of space:
`HackIOException` with `ErrorCode` `NoSpace` (`ENOSPC`: browser quota
reached, or root reserve reached for a simple user) or
`FileTooLarge` (`EFBIG`, over 16 MiB); `ShellErrorText.Describe` gives the
Linux message ([`system-backup.md`](../dev/system-backup.md)). An open beyond
the process's `nofile` limit throws `HackIOException` with
`TooManyOpenFiles` (`EMFILE`, SDK 1.2); a launch beyond `nproc`
fails with "Resource temporarily unavailable" (`EAGAIN`)
([`process-limits.md`](../dev/process-limits.md)).

Detailed examples by domain: [`hack-file-api.md`](hack-file-api.md),
[`settings-files.md`](settings-files.md), [`logging.md`](../dev/logging.md),
[`trash.md`](../dev/trash.md), [`process-environment.md`](../dev/process-environment.md),
[`sdk-network.md`](sdk-network.md), [`task-manager.md`](../dev/task-manager.md),
[`shell-host.md`](shell-host.md), [`system-administration.md`](system-administration.md).

### Heavy computation: `HackBackground`

A windowed app runs on the main thread, which also draws the desktop: a long
loop there freezes everything. `HackBackground.RunAsync` hands the work to a
child process of the app, in a Web Worker when the host has one, and waits for its
result; progress comes back in the window's context.

```csharp
[assembly: HackWireContext(typeof(MyJobsJsonContext))]

[JsonSerializable(typeof(PrimeCountInput))]
[JsonSerializable(typeof(PrimeCountResult))]
internal sealed partial class MyJobsJsonContext : JsonSerializerContext;

public static class MyJobs
{
    public static async Task<PrimeCountResult> CountPrimes(PrimeCountInput input, HackBackgroundContext context)
    {
        // ... calcul, avec context.Report(fraction, message) et await context.CheckpointAsync() ...
    }
}

PrimeCountResult result = await HackBackground.RunAsync<PrimeCountInput, PrimeCountResult>(
    MyJobs.CountPrimes, new PrimeCountInput(3_000_000), new Progress<HackBackgroundProgress>(Show), cancellationToken: ct);
```

The job is a **static method** of the app (no lambda: it cannot
capture the window's objects) and its types go through JSON via a
`HackWireContext` context. The SDK analyzer checks this at build
(`HOSBG0001` to `HOSBG0004`). Cancellation: `OperationCanceledException`; job
killed from `kill` or the task manager: `HackProcessKilledException`
(to be caught **before** cancellation); exception from the job:
`HackBackgroundJobException`. When the app closes, the job sees its token
canceled and has 5 s to finish before being killed (ADR 0147). In the app's tests, the job runs
inline with the same serialization. Complete guide and example:
[`background-jobs.md`](background-jobs.md) and the "Background Task" section of
`Apps/Samples/HackerOs.Samples.WindowApp`.

### Adding entries to the file menus

An app can add "Convert to PNG", "Extract here"... to the file menus
across the whole OS (explorer, file dialogs, desktop): it
requests the `shell.context-menu` capability and declares in its
`[AppManifest]` class a static `[RegisterFileContextMenu]` method that calls
`context.Register(title, name, intent: …, types: […], after: HackFileMenuItems.Open)`.
The title is a key of `Resources/AppManifest.resx` (English, required) and
of `Resources/AppManifest.fr.resx`. The method runs at build time; on click,
the app receives the intent and reads the paths through `HackFileMenuIntent.ReadPaths`.
Complete guide: [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4).

### Dangerous capabilities

`filesystem.system.*`, `apps.manage`, `process.manage`, `services.manage`,
`network.real.access`, `shell.host`, `permissions.manage`... are
**dangerous**: for a non-system app, the OS asks an administrator
to approve them at first launch, and the app can be launched without them if
the administrator refuses. The code must therefore expect
`HackUnauthorizedAccessException` and degrade gracefully. Details:
[`capabilities.md`](capabilities.md),
[`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md).

## 7. Installing the app in the OS

Add a `ProjectReference` in `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`:

```xml
<ProjectReference Include="..\..\Apps\Samples\Example.Notes\Example.Notes.csproj" />
```

That is all. At build time, the host aggregates the manifest into
`BuiltInApplications.json` and loads the app's assembly at first launch.
`IsSystemApp="true"` on the reference makes the app a system app (not
uninstallable, dangerous capabilities granted automatically): reserved for the OS's
base apps.

## 8. Testing an app

`Tests/HackerOs.Tests.Support` provides `MinimalAppExecutionContext` and fake
gateways (`FakeAppFileSystemGateway`, `FakeAppClockGateway`,
`FakeAppLoggingGateway`, `FakeAppIntentGateway`, `FakeAppEnvironmentGateway`,
`FakeShellSession`...). The manifest comes from the definition itself.

```csharp
public sealed class WordCountCommandTests
{
    private static readonly AppManifest Manifest = AppDefinitionMapper.ToManifest(new WordCountApp());

    [Fact]
    public async Task Counts_the_words_of_a_file()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithDirectory("/home/alice")
            .WithFile("/home/alice/notes.txt", "un deux trois");
        MinimalAppExecutionContext app = new(Manifest, files, userId: "alice");
        StringWriter output = new();
        TerminalExecutionContext terminal = new(
            app, ["notes.txt"], TextReader.Null, output, TextWriter.Null, "/home/alice",
            new Dictionary<string, string>());

        // L'OS ouvre le contexte ambiant autour d'ExecuteAsync ; un test le fait lui-même.
        using (HackExecutionContext.Enter(app, "/home/alice"))
        {
            int exitCode = await new WordCountCommand(Manifest).ExecuteAsync(terminal, CancellationToken.None);
            Assert.Equal(0, exitCode);
        }

        Assert.Equal("       3 notes.txt", output.ToString().TrimEnd());
    }
}
```

The test project references the app's project and `HackerOs.Tests.Support`
(xUnit 2.9). Windowed components are tested with bUnit (see
`Tests/HackerOs.Apps.*.Tests`).

## 8 bis. Translating your app

HackerOS speaks English (default) and French; each process follows its
own `LANG` (ADR 0104). To translate an app:

1. Add a marker class `internal sealed class Strings` in the root
   namespace, and its texts in `Resources/Strings.resx` (English) and
   `Resources/Strings.fr.resx` (French), with exactly the same keys
   (`build/tools/resx.py` writes a `.resx` from a JSON table).
2. Read the texts in the process's language:

   ```razor
   @using HackerOs.AppSdk.Globalization
   @using Microsoft.Extensions.Localization

   <button aria-label="@L["Toolbar.NewFolder"]">@L["Toolbar.NewFolder"]</button>

   @code {
       private IStringLocalizer<Strings>? _l;
       private IStringLocalizer<Strings> L => _l ??= AppContext.GetLocalizer<Strings>();
   }
   ```

   In a command: `context.App.GetLocalizer<Strings>()`. Displayed dates and
   numbers use `AppContext.FormatCulture`.
3. Translate the name and description: `Resources/AppManifest.fr.resx` with the
   keys `Name`, `Description` (and `Terminal.Usage` for a command). The build
   produces `localization/fr.json` and the manifest's `localizations` entry; the
   launcher and `help` then display the translated name.

Output read by scripts, file names and logs stay
in English. The complete guide is [`localization.md`](localization.md).

## 9. Checklist before delivering

- [ ] The project is under `Apps/`, in `HackerOs.sln`, and only references the SDK.
- [ ] The `[AppManifest]` class only requests the necessary capabilities.
- [ ] The regenerated `manifest.json` (or `manifests/*.json`) is committed.
- [ ] No inline CSS or JavaScript; styles use the `--hos-*` tokens.
- [ ] Permission and file errors are handled and displayed.
- [ ] A stop request loses nothing silently (`OnAppStopRequestedAsync`, `OnStoppingAsync`) and long loops call `HackProcess.CheckpointAsync` (ADR 0069).
- [ ] Tests cover the app's logic.
- [ ] Visible texts are in `Resources/Strings.resx` and `Strings.fr.resx` (same keys), the name in `AppManifest.fr.resx`.
- [ ] The app works with the Ecosystem host alone, without a server.
- [ ] A document describes the app under `docs/`, added to the index [`README.md`](../README.md).

A host from another repository (`hackeros-host` template) installs an app in the same
way: `ProjectReference` or `PackageReference` (app package produced by
`dotnet pack`, which embeds its manifests), with the same `IsSystemApp`
metadata.

## Tasks of this document

- [x] Present the three kinds of app and their entry points.
- [x] Project creation, allowed references.
- [x] `AppDefinition`: useful members, generator rules.
- [x] Complete example of a windowed app (files, settings, log, dialog, notification, CSS).
- [x] Example of a shell command (arguments, streams, exit code).
- [x] Example of a service (loop, simulated clock, cancellation).
- [x] Table of OS services and associated capabilities.
- [x] Installation in the host and unit test of a command.
- [x] Compile the examples and run the test against `rebuild/base`.
- [x] Build an app or a host from another repository with the local packages and templates (v3/6).
- [x] Complete guide for the third-party repository, SDK 1.0 (`v3/15`, ADR 0114): [`building-on-hackeros.md`](building-on-hackeros.md).
- [x] Machine backup and `NoSpace`/`FileTooLarge` errors (`v3/7-storage-backup`).
- [x] Translating your app: resources, `GetLocalizer<T>`, `AppManifest.fr.resx` (`v3/12`, ADR 0104).
- [x] Heavy computation outside the window with `HackBackground` (`exec/5`, ADR 0128 and 0140).
- [x] SDK 1.2: `HackWireContext` of a service in a worker, `OomScoreAdjust`, process limits, `TooManyOpenFiles` (`exec/10-final`).
- [x] SDK 2.0: 2.0.0 templates, `HackFileView` and `HackFileOperations` in the services table (`desk/6-sdk-2`, ADR 0161); migrating a 1.x app in [`sdk-packages.md`](sdk-packages.md#sdk-20).
