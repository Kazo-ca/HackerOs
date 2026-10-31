> [English version](../../../en/wasm/framework/sdk-developer-guide.md)

# Guide du développeur d'apps HackerOS

## Objet

Ce guide explique comment écrire une app pour HackerOS avec le SDK public :
une fenêtre, une commande du shell ou un service. Il part de zéro et va
jusqu'à l'installation et aux tests. La liste complète des API est dans la
[référence du SDK](sdk-reference.md) ; les règles de la frontière du SDK sont
dans [`app-sdk.md`](app-sdk.md).

Tous les exemples de ce guide ont été compilés et le test de la dernière
section exécuté contre le code de `rebuild/base` (2026-10-11).

## Les trois genres d'app

| Genre (`AppKind`) | Point d'entrée | Exemples dans l'OS |
|---|---|---|
| `Window` | composant Razor dérivé de `WindowAppBase` | Text Editor, File Explorer, Calculator |
| `Terminal` | classe dérivée de `TerminalAppBase`, lancée par le shell | `ls`, `cat`, `grep`, `nano` |
| `Service` | classe dérivée de `ServiceAppBase`, sans interface, liée à la session | services démarrés à la connexion |
| `Web` | classe dérivée de `WebServerAppBase`, URL dans `Urls` (`web.urls`), démarrée à la première requête dans le worker `web` partagé ; avec ASP.Hack, une `WebAppDefinition` | sites internes `.hack` : [guide ASP.Hack](asp-hack-developer-guide.md), exemple Intranet |

Une app est décrite par une classe `[AppManifest]` dérivée d'`AppDefinition`.
Le build en tire `manifest.json` ; l'OS ne lit que ce fichier, jamais la classe.

Des apps complètes servent de modèles dans `Apps/Samples/` (une par genre) et
`Apps/System/` (les apps de base, écrites avec le même SDK public).

## 1. Créer le projet

Un projet d'app vit sous `Apps/` : c'est ce qui en fait un projet d'app
(`HackerOsAppProject`), avec génération du manifeste et contrôle de la
frontière du SDK.

`Apps/Samples/Example.Notes/Example.Notes.csproj` :

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

- Une commande ou un service sans interface peut utiliser
  `Microsoft.NET.Sdk` et se passer de `HackerOs.AppSdk.Blazor`.
- Références permises : les quatre `Shared/*.Abstractions`, les bibliothèques
  `Shared/HackerOs.AppSdk*`, `Platform/HackerOs.Theming.Blazor` et d'autres
  projets sous `Apps/`. Toute autre référence de projet échoue au build
  (`HOSAPP0030`). Les `PackageReference` (MudBlazor par exemple) sont libres.
- Ajouter le projet à `HackerOs.sln` (`dotnet sln add`).

### Depuis un autre dépôt (paquets locaux)

Hors de ce dépôt, une app se construit sur les paquets locaux (ADR 0098 §2,
[`sdk-packages.md`](sdk-packages.md)) : `build/pack-local.sh` (ou `.ps1`) les
produit dans `artifacts/local-nupkg`, un `nuget.config` (modèle
`build/nuget.config.sample`) pointe vers ce dossier, puis les modèles créent le
projet :

```bash
dotnet new install <HackerOS>/artifacts/local-nupkg/HackerOs.Templates.2.2.0.nupkg
dotnet new hackeros-window -n Contoso.Notes --appId com.contoso.notes
dotnet new hackeros-host -n Contoso.Os          # un hôte qui lance tout l'OS
```

Le projet référence `HackerOs.AppSdk*` et `HackerOs.App.Build`
(`PrivateAssets="all"`), qui apporte la génération du manifeste, le générateur
de routes ASP.Hack et la règle de frontière sur les paquets (`HOSAPP0030`).
Un hôte ajoute l'app par une `ProjectReference` ou une `PackageReference`.
Le guide complet d'un dépôt tiers (disposition, version épinglée, hôte, tests,
publication, montée de version, ce que la promesse du SDK 1.0 couvre) est
[`building-on-hackeros.md`](building-on-hackeros.md).

## 2. Décrire l'app : `AppDefinition`

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

Seuls `Id`, `Name`, `Kind` et `EntryPoint` sont obligatoires ; les autres
membres ont une valeur par défaut (tableau complet dans
[`app-model.md`](app-model.md#appdefinition-et-appmanifest)). Les membres les
plus utiles :

| Membre | Rôle |
|---|---|
| `Capabilities` | Droits demandés, pris dans `AppCapabilities` ([référence](sdk-reference.md#capacités)). Ne demander que le nécessaire : une capacité dangereuse impose une approbation administrateur au premier lancement. |
| `FileHandlers` | Types de fichiers que l'app sait ouvrir ; le chemin arrive dans `LaunchFile`. |
| `Terminal` | Pour une commande : nom, usage et alias (`new("wc-notes", "wc-notes [-l] FILE...", ["wcn"])`). |
| `Settings` | Schéma des paramètres lus par `HackSettings`. |
| `SingleInstancePerUser` | Une seule fenêtre ; un second lancement l'active. |
| `AutoStart` | Pour un service : démarré à la connexion. |
| `LaunchVisibility` | `Hidden` pour ne pas apparaître dans le menu Démarrer (commandes, services). |
| `Icons` | Images relatives à `wwwroot/`, aussi déclarées dans `Assets` ; la première apparaît dans la barre de titre et la taskbar (ADR 0164). macOS masque celle du titre. Le build calcule l'empreinte de l'asset. |
| `Category`, `Version` | Présentation et classement de l'app. |
| `OomScoreAdjust` | `oom_score_adj` de départ (SDK 1.2) ; une valeur négative, qui protège de l'OOM killer, est réservée aux apps système (`HOSAPP0024`, [`oom-killer.md`](../dev/oom-killer.md)). |

Règles imposées par le générateur : classe publique, non abstraite, avec un
constructeur sans paramètre ; getters purs (le générateur instancie la classe
deux fois et compare) ; point d'entrée dans le même assembly. Un projet peut
contenir plusieurs classes `[AppManifest]` : le build écrit alors
`manifests/<appId>.json` au lieu de `manifest.json`.

Le fichier généré est **suivi par git** : un changement de capacité se voit
dans la revue. Ne jamais le modifier à la main, le build l'écrase.

## 3. Une app fenêtrée

Le point d'entrée est un composant Razor qui hérite de `WindowAppBase`. CSS et
JavaScript vont dans des fichiers à côté du composant, jamais en ligne (le
build refuse `style="..."`, `<style>`, `<script>` et `onclick="..."`).

`NotesWindow.razor` :

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

`NotesWindow.razor.cs` :

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

`NotesWindow.razor.css` utilise les jetons du thème (`--hos-*`), pour suivre
le thème choisi par l'utilisateur :

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

### Ce que fournit `WindowAppBase`

| Membre | Rôle |
|---|---|
| `AppContext` | Contexte d'exécution de l'instance (`IAppExecutionContext`) : passerelles, identité, capacités accordées. |
| `LaunchArguments`, `LaunchFile` | Arguments du processus sans `argv[0]` ; premier argument s'il est un chemin absolu. |
| `OnAppInitialized[Async]`, `OnAppParametersSet[Async]`, `OnAppAfterRender[Async]`, `ShouldRenderApp` | Cycle de vie. Les méthodes Blazor (`OnInitializedAsync`…) sont scellées : on surcharge celles-ci. |
| `OpenFileAsync`, `SaveFileAsync`, `SelectFolderAsync` | Dialogues de fichiers de l'OS (capacités `dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select`) ; le résultat porte le chemin choisi (`Resource.Path`, `Resources`). |
| `MessageBox`, `TextInput` | Boîte de message et saisie de texte. `MessageBox(titre, message, MessageBoxType.Ok, MessageBoxIcon.Error)` affiche une erreur avec son icône (prochaine version du SDK). |
| `WindowTitle`, `SetWindowTitleAsync`, `FormatWindowTitle` | Titre de la fenêtre (ADR 0117, SDK 1.1) : l'app écrit tout le titre, par convention `document — App` avec `*` en tête si le document est modifié ; `null` rend le nom de l'app. Voir ci-dessous. |
| `OnPermissionErrorRaised` | Surcharger pour afficher soi-même une erreur de permission (mettre `ErrorHandled = true`) au lieu de la notification de l'OS. |
| `OnAppStopRequestedAsync(e)` | L'OS demande l'arrêt (`kill`, « End task », déconnexion ; ADR 0069) : finir ou annuler l'opération en cours, écrire un brouillon des modifications non enregistrées (`HackRecoveryDraft`). La fenêtre se ferme quand la tâche renvoyée se termine, au plus tard à `e.Deadline`. |

Le composant peut aussi implémenter :

- `IWindowCloseGuard` (`ConfirmCloseAsync`) pour demander confirmation avant
  fermeture, par exemple s'il reste des modifications non enregistrées
  (CodeEditor le fait) ;
- `IAppBackHandler` pour gérer le bouton Retour (mobile, barre de titre, bouton Précédent de la souris, Alt+←, Retour du téléphone) et `IAppForwardHandler` pour le bouton Suivant de la souris et Alt+→ ([ADR 0220](../dev/adr/0220-mouse-back-forward-buttons.md)) ;
  déclarer alors `SupportsBack => true`.

### Événements et taille de la fenêtre

Surcharger `OnWindowActivated[Async]`, `OnWindowDeactivated[Async]`,
`OnWindowStateChanged[Async](previous, current)` et `OnWindowResized[Async](size)`
pour réagir au focus, à la réduction, à l'agrandissement, à la restauration et à
la fin d'un redimensionnement ; `IsWindowActive`, `WindowState` et `WindowSize`
donnent le dernier état. Une exception dans un crochet est écrite dans
`/var/log/apps/<appId>.log` sans bloquer la fenêtre.

La taille de la fenêtre se déclare dans la définition :

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 720, DefaultHeight = 520, MinWidth = 480, MinHeight = 360,
};
```

Le titre se pose depuis l'app (SDK 1.1, ADR 0117). Le poser dans
`OnAppInitialized[Async]` l'affiche avec la fenêtre ; le reposer après chaque
rendu le garde à jour (redemander le même titre ne fait rien) :

```csharp
protected override Task OnAppAfterRenderAsync(bool firstRender) =>
    SetWindowTitleAsync(FormatWindowTitle(_fileName, _isDirty)).AsTask(); // « *notes.txt — Mon app »
```

Sans déclaration, la fenêtre fait 800×560 avec un minimum de 360×240.
`Resizable = false` retire les poignées et le bouton Agrandir. L'OS retient la
taille et l'état agrandi de la fenêtre d'une ouverture à l'autre, pas sa
position (ADR 0122) ; `RememberGeometry = false` (SDK 1.1) la fait toujours
s'ouvrir à sa taille par défaut ([`window-geometry.md`](../dev/window-geometry.md)). Détails, ordre
des crochets et raccourcis clavier du gestionnaire de fenêtres :
[`window-model.md`](window-model.md).

### Lancée depuis le terminal

Taper le dernier segment de l'identifiant de l'app dans le terminal la lance
(ADR 0064) : `my-app --verbose notes.txt` pour `com.example.my-app`. Les mots
qui suivent arrivent dans `LaunchArguments` (relatifs au dossier du shell :
`HackPath.GetFullPath` les résout), et `HackConsole` écrit dans ce terminal,
comme `printf` d'un programme graphique Linux :

```csharp
protected override void OnAppInitialized()
{
    if (HackConsole.IsAttached)
    {
        HackConsole.WriteLine($"my-app: opening {string.Join(' ', LaunchArguments)}");
    }
}
```

Le shell attend la fermeture de la fenêtre (`my-app &` rend l'invite) ;
lancée du bureau, l'app écrit dans `/dev/null`. Voir
[`shell-interpreter.md`](../dev/shell-interpreter.md#apps-lancées-par-leur-nom).

### Contexte ambiant et composants enfants

Les façades `Hack*` savent quelle app les appelle grâce à un contexte ambiant
que l'OS ouvre autour du cycle de vie, des gestionnaires d'événements
(`@onclick`…) et du rendu de `WindowAppBase`. **Tout composant enfant** hérite
de `HackComponentBase` : il reçoit le même contexte en cascade. Depuis la
phase 5, c'est vrai même s'il n'appelle pas `Hack*` : le temps du code exécuté
hors contexte est compté au compositeur, pas à l'app (ADR 0067), et un test
(`AmbientFrameSourceTests`) le vérifie pour les apps de `Apps/`. Un dialogue
MudBlazor, rendu hors de la fenêtre, reçoit le contexte de la fenêtre en
paramètre et l'adopte avant le cycle de vie de `HackComponentBase` (comme le
faisait `FileExplorerDialogBase`, retiré par l'ADR 0155 : les dialogues de
fichiers de l'OS passent maintenant par `IDialogService`). Une méthode `[JSInvokable]` ou un
rappel de minuterie .NET (hors `AppContext.Clock`) ouvre le contexte lui-même,
dès sa première instruction :

```csharp
using (HackExecutionContext.Enter(AppContext))
{
    await HackFile.WriteAllTextAsync("~/autosave.txt", _text);
}
```

Sans contexte, l'API lève `HackExecutionContextMissingException` ; elle ne
retombe jamais sur une identité système.

### JavaScript

À n'utiliser que pour une API du navigateur inaccessible en C#. Le module est
un fichier `MonComposant.razor.js` à côté du composant (ou sous `wwwroot/` du
projet), importé ainsi et uniquement ainsi (vérifié au build, `HOSAPP0031`) :

```csharp
[Inject] private IJSRuntime JS { get; set; } = default!;

_module = await JS.InvokeAsync<IJSObjectReference>(
    "import", "./_content/Example.Notes/NotesWindow.razor.js");
```

Ne jamais ajouter de `<script>` dans un hôte.

### Ne pas bloquer le fil principal (ADR 0127 §4)

Une app fenêtrée tourne sur le fil principal du navigateur, comme tout le
bureau. Une boucle longue sans `await` gèle alors tout l'OS : la souris, les
autres fenêtres et même la boîte « L'application ne répond pas », qui ne peut
s'afficher qu'après le gel. Mettez un calcul lourd dans
`HackBackground.RunAsync` (ADR 0128, il tourne dans un worker), ou appelez
`HackProcess.CheckpointAsync` souvent dans la boucle. Quand la fenêtre reste
occupée plus de 5 s, l'OS lui ajoute « (Not responding) » et propose
« Attendre » ou « Terminer l'application » (arrêt demandé puis forcé après
2 s). Voir [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md).

## 4. Une commande du shell

Une commande reçoit ses arguments, ses flux et le répertoire courant du shell.
Elle rend un code de sortie (0 = succès), lu par `$?` et `&&`.

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

`TerminalExecutionContext` donne aussi `Environment` (variables du
processus), `App` (le contexte d'app), `Shell` (`IShellSession`, pour une
commande qui agit sur son shell comme `cd` ou `export`, capacité
`shell.control`) et `FullScreen` (écran alternatif plein terminal, comme
`nano`). Les erreurs vont sur `StandardError`, les messages dans le style des
outils GNU. Voir [`shell-commands.md`](../dev/shell-commands.md) et
[`shell-interpreter.md`](../dev/shell-interpreter.md).

Une boucle longue (parcours d'arborescence, gros fichier ligne à ligne) appelle
`await HackProcess.CheckpointAsync(cancellationToken)` à chaque tour (ADR
0069) : elle rend la main au-delà de 8 ms, ce qui garde l'OS fluide, et
s'arrête dès que `kill` le demande. Sans arrêt demandé et dans sa tranche,
l'appel ne coûte qu'une lecture d'horloge. Une commande arrêtée sur son jeton
sort avec `130` ; une commande tuée (`kill -9`, ou délai de 5 s dépassé) avec
`137`, et ses continuations en attente ne s'exécutent plus.

## 5. Un service

Un service tourne en arrière-plan pendant la session, sans fenêtre. Il observe
le jeton d'annulation (déconnexion, arrêt, désactivation) et peut libérer ses
ressources dans `OnStoppingAsync`.

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

`OnStoppingAsync(reason, token)` est appelé une seule fois, dès que l'OS
demande l'arrêt (ADR 0069), avant l'annulation du jeton :
`ServiceStopReason.Terminated` pour `kill`, « End task » ou `systemctl stop`,
`Logout`, `Shutdown` ou `Disabled` sinon. C'est là qu'un service qui tient un
état termine ou annule son opération en cours.

États et santé (ADR 0097, ADR 0107) : le service est `activating` tant que
`OnStartAsync(context, token)` (facultatif : ouvrir un port, charger un cache)
n'est pas revenu, puis `active`. Un démarrage de plus de 30 s le fait tuer en
`failed (Result: timeout)`. Une exception non gérée, un code de sortie non nul
ou un `kill -9` le laissent `failed` avec la raison, jusqu'au prochain
démarrage ou à `systemctl reset-failed`. Pendant qu'il tourne, il peut dire
qu'il va mal sans s'arrêter :

```csharp
ReportHealth(HackServiceHealth.Degraded, "database unreachable");
```

Démarrage automatique (`AutoStart => true`, mode `Automatic`) : après chaque
connexion, sous l'utilisateur ; arrêté à la déconnexion. Une **app système**
peut déclarer `public override AppServiceStartup? ServiceStartup =>
AppServiceStartup.Boot;` : elle démarre alors à la fin du boot, avant l'écran
de connexion, sous le compte `hackeros-svc` (maison `/var/lib/hackeros-svc`),
continue après la déconnexion et ne s'arrête qu'à l'extinction. Une autre app
qui demande `Boot` démarre à la connexion, avec un avertissement dans
`/var/log/syslog`.

Le service apparaît dans l'onglet Services du gestionnaire de tâches et dans
`/proc`. Il communique avec les autres apps par les sujets du bus
d'événements (`context.Events.Publish(topic, payload)` dans son propre espace
de noms, ADR 0038) : voir `Apps/Samples/HackerOs.Samples.ServiceApp`. Un
service tourne par défaut dans un Web Worker dédié (ADR 0070) : le type de ses
charges doit alors être déclaré par `[assembly: HackWireContext(typeof(…))]`
(SDK 1.2, ADR 0124), comme le fait `SampleTickerJsonContext` de l'exemple ;
voir [`app-sdk.md`](app-sdk.md#processus-dans-des-web-workers-adr-0070-et-0071).

## 6. Utiliser les services de l'OS

Une app n'obtient un service de l'OS que par le SDK : une **façade statique**
`Hack*` (forme recommandée, elle lit le contexte ambiant) ou une **passerelle**
de `AppContext`. L'injection d'un service de la plateforme est refusée au
build (`HOSAPP0031`). Les tâches courantes :

| Besoin | API | Capacité |
|---|---|---|
| Lire, écrire, copier, déplacer des fichiers | `HackFile`, `HackDirectory`, `HackPath`, `HackFileInfo` | `filesystem.user-home.*` pour `~`, `filesystem.system.*` (dangereuses) ailleurs |
| Copier ou déplacer comme l'explorateur (conflits de noms, « (copie) », annulation) | `HackFileOperations.CopyAsync`, `MoveAsync`, `ImportAsync` ; `IDialogService.AsFileConflictResolver(AppContext)` pose la question (SDK 2.0, ADR 0161 §2) | celles des fichiers |
| Mettre à la corbeille | `HackTrash.MoveToTrashAsync` | celles du fichier |
| Afficher des fichiers dans sa fenêtre (liste, icônes, arbre) | `<HackFileView>` (contrat `hackeros.file-view`, fourni par l'explorateur ou l'app choisie par l'administrateur, [`embedded-components.md`](embedded-components.md)) | celles du dossier |
| Surveiller un dossier | `HackFileSystemWatcher` | celles du dossier |
| Paramètres de l'app | `HackSettings.OpenAsync`, `GetAsync`, `SetAsync` | `filesystem.private.read` / `.write` |
| Garder un mot de passe ou un jeton | `HackKeyring.SetAsync`, `GetAsync`, ou une clé `SecretReference` avec `HackSettingsDocument.SetSecretAsync` ([`sensitive-settings.md`](../dev/sensitive-settings.md)) | aucune pour ses propres entrées |
| Journaliser | `AppContext.Logging.Log` → `/var/log/apps/<appId>.log` | aucune |
| Notifier l'utilisateur | `AppContext.Notifications.Post` | `notifications.post` |
| Utilisateur, dossiers spéciaux, variables, arguments | `HackUser`, `HackEnvironment` | aucune |
| Lancer une app, ouvrir un fichier avec son app | `HackApps.LaunchAsync`, `HackApps.OpenFileAsync` | `apps.launch` |
| Ouvrir une page dans le navigateur, ou dans une fenêtre détachée | `HackBrowser.OpenAsync`, `HackBrowser.OpenDetachedAsync` | `apps.launch` |
| Afficher une partie de sa fenêtre dans une fenêtre détachée | `IDisplaySurfaceService` (injecté) et `<DisplaySurfaceContent>` | aucune |
| Presse-papiers | `HackClipboard` | `clipboard.read` / `.write` |
| Réseau simulé (et réel si l'OS l'autorise) | `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient`, `HackUdpClient` | `network.simulated.*`, `network.real.access` (dangereuse) |
| Processus et services | `HackProcess`, `HackServiceController` | `process.list`, `process.manage`, `services.manage` |
| Lire ou baisser les limites de son processus (`ulimit`) | `HackProcess.GetResourceLimitsAsync`, `SetResourceLimitAsync` (SDK 1.2, [`process-limits.md`](../dev/process-limits.md)) | aucune pour son processus ; `process.manage` pour un autre |
| Héberger un shell (terminal, éditeur de code) | `HackShell.StartAsync` | `shell.host` (dangereuse) |
| Sauvegarder ou restaurer toute la machine (apps système) | `HackSystemBackup.CreateAsync`, `RestoreAsync` | `system.backup` (dangereuse) + administrateur |
| Temps simulé, minuteries | `AppContext.Clock` | aucune |
| Sortir un calcul lourd de la fenêtre (filtre, recherche, compression) | `HackBackground.RunAsync` (SDK 1.2) | aucune ; la tâche a les capacités de l'app |

Les chemins acceptent `~` et les chemins relatifs (au répertoire courant du
processus). Les erreurs suivent `System.IO` : `HackFileNotFoundException`,
`HackDirectoryNotFoundException`, `HackFileConflictException` (révision
concurrente), `HackIOException`, `HackUnauthorizedAccessException` (avec
`RequiredCapability`). Une écriture peut échouer faute de place :
`HackIOException` avec `ErrorCode` `NoSpace` (`ENOSPC` : quota du navigateur
atteint, ou réserve de root atteinte pour un utilisateur simple) ou
`FileTooLarge` (`EFBIG`, plus de 16 Mio) ; `ShellErrorText.Describe` donne le
message Linux ([`system-backup.md`](../dev/system-backup.md)). Une ouverture au-delà
de la limite `nofile` du processus lève `HackIOException` avec
`TooManyOpenFiles` (`EMFILE`, SDK 1.2) ; un lancement au-delà de `nproc`
échoue avec « Resource temporarily unavailable » (`EAGAIN`)
([`process-limits.md`](../dev/process-limits.md)).

Exemples détaillés par domaine : [`hack-file-api.md`](hack-file-api.md),
[`settings-files.md`](settings-files.md), [`logging.md`](../dev/logging.md),
[`trash.md`](../dev/trash.md), [`process-environment.md`](../dev/process-environment.md),
[`sdk-network.md`](sdk-network.md), [`task-manager.md`](../dev/task-manager.md),
[`shell-host.md`](shell-host.md), [`system-administration.md`](system-administration.md).

### Calculs lourds : `HackBackground`

Une app fenêtrée tourne sur le fil principal, qui dessine aussi le bureau : une
boucle longue y gèle tout. `HackBackground.RunAsync` confie le travail à un
processus enfant de l'app, dans un Web Worker quand l'hôte en a, et attend son
résultat ; la progression revient dans le contexte de la fenêtre.

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

La tâche est une **méthode statique** de l'app (pas de lambda : elle ne peut pas
capturer les objets de la fenêtre) et ses types passent en JSON par un
contexte `HackWireContext`. L'analyseur du SDK le vérifie au build
(`HOSBG0001` à `HOSBG0004`). Annulation : `OperationCanceledException` ; tâche
tuée depuis `kill` ou le gestionnaire de tâches : `HackProcessKilledException`
(à attraper **avant** l'annulation) ; exception de la tâche :
`HackBackgroundJobException`. Quand l'app se ferme, la tâche voit son jeton
annulé et a 5 s pour finir avant d'être tuée (ADR 0147). Dans les tests de l'app, la tâche s'exécute en
ligne avec la même sérialisation. Guide complet et exemple :
[`background-jobs.md`](background-jobs.md) et la section « Background Task » de
`Apps/Samples/HackerOs.Samples.WindowApp`.

### Ajouter des entrées aux menus des fichiers

Une app peut ajouter « Convertir en PNG », « Extraire ici »… aux menus des
fichiers de tout l'OS (explorateur, dialogues de fichiers, bureau) : elle
demande la capacité `shell.context-menu` et déclare dans sa classe
`[AppManifest]` une méthode statique `[RegisterFileContextMenu]` qui appelle
`context.Register(title, name, intent: …, types: […], after: HackFileMenuItems.Open)`.
Le titre est une clé de `Resources/AppManifest.resx` (anglais, obligatoire) et
de `Resources/AppManifest.fr.resx`. La méthode s'exécute au build ; au clic,
l'app reçoit l'intent et lit les chemins par `HackFileMenuIntent.ReadPaths`.
Guide complet : [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4).

### Capacités dangereuses

`filesystem.system.*`, `apps.manage`, `process.manage`, `services.manage`,
`network.real.access`, `shell.host`, `permissions.manage`… sont
**dangereuses** : pour une app non système, l'OS demande à un administrateur
de les approuver au premier lancement, et l'app peut être lancée sans elles si
l'administrateur refuse. Le code doit donc s'attendre à
`HackUnauthorizedAccessException` et dégrader proprement. Détails :
[`capabilities.md`](capabilities.md),
[`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md).

## 7. Installer l'app dans l'OS

Ajouter une `ProjectReference` dans `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj` :

```xml
<ProjectReference Include="..\..\Apps\Samples\Example.Notes\Example.Notes.csproj" />
```

C'est tout. Au build, l'hôte agrège le manifeste dans
`BuiltInApplications.json` et charge l'assembly de l'app au premier lancement.
`IsSystemApp="true"` sur la référence fait de l'app une app système (non
désinstallable, capacités dangereuses accordées d'office) : réservé aux apps
de base de l'OS.

## 8. Tester une app

`Tests/HackerOs.Tests.Support` fournit `MinimalAppExecutionContext` et des
passerelles factices (`FakeAppFileSystemGateway`, `FakeAppClockGateway`,
`FakeAppLoggingGateway`, `FakeAppIntentGateway`, `FakeAppEnvironmentGateway`,
`FakeShellSession`…). Le manifeste vient de la définition elle-même.

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

Le projet de tests référence le projet de l'app et `HackerOs.Tests.Support`
(xUnit 2.9). Les composants fenêtrés se testent avec bUnit (voir
`Tests/HackerOs.Apps.*.Tests`).

## 8 bis. Traduire son app

HackerOS parle anglais (par défaut) et français ; chaque processus suit son
propre `LANG` (ADR 0104). Pour traduire une app :

1. Ajouter une classe marqueur `internal sealed class Strings` dans l'espace
   de noms racine, et ses textes dans `Resources/Strings.resx` (anglais) et
   `Resources/Strings.fr.resx` (français), avec exactement les mêmes clés
   (`build/tools/resx.py` écrit un `.resx` depuis une table JSON).
2. Lire les textes dans la langue du processus :

   ```razor
   @using HackerOs.AppSdk.Globalization
   @using Microsoft.Extensions.Localization

   <button aria-label="@L["Toolbar.NewFolder"]">@L["Toolbar.NewFolder"]</button>

   @code {
       private IStringLocalizer<Strings>? _l;
       private IStringLocalizer<Strings> L => _l ??= AppContext.GetLocalizer<Strings>();
   }
   ```

   Dans une commande : `context.App.GetLocalizer<Strings>()`. Les dates et
   nombres affichés utilisent `AppContext.FormatCulture`.
3. Traduire le nom et la description : `Resources/AppManifest.fr.resx` avec les
   clés `Name`, `Description` (et `Terminal.Usage` pour une commande). Le build
   produit `localization/fr.json` et l'entrée `localizations` du manifeste ; le
   lanceur et `help` affichent alors le nom traduit.

Les sorties lues par des scripts, les noms de fichiers et les journaux restent
en anglais. Le guide complet est [`localization.md`](localization.md).

## 9. Liste de contrôle avant de livrer

- [ ] Le projet est sous `Apps/`, dans `HackerOs.sln`, et ne référence que le SDK.
- [ ] La classe `[AppManifest]` ne demande que les capacités nécessaires.
- [ ] `manifest.json` (ou `manifests/*.json`) régénéré est committé.
- [ ] Pas de CSS ni de JavaScript en ligne ; styles sur les jetons `--hos-*`.
- [ ] Les erreurs de permission et de fichier sont gérées et affichées.
- [ ] Une demande d'arrêt ne perd rien en silence (`OnAppStopRequestedAsync`, `OnStoppingAsync`) et les boucles longues appellent `HackProcess.CheckpointAsync` (ADR 0069).
- [ ] Des tests couvrent la logique de l'app.
- [ ] Les textes visibles sont dans `Resources/Strings.resx` et `Strings.fr.resx` (mêmes clés), le nom dans `AppManifest.fr.resx`.
- [ ] L'app fonctionne avec le seul hôte Ecosystem, sans serveur.
- [ ] Un document décrit l'app sous `docs/`, ajouté à l'index [`README.md`](../README.md).

Un hôte d'un autre dépôt (modèle `hackeros-host`) installe une app de la même
façon : `ProjectReference` ou `PackageReference` (paquet d'app produit par
`dotnet pack`, qui embarque ses manifestes), avec la même métadonnée
`IsSystemApp`.

## Tâches de ce document

- [x] Présenter les trois genres d'app et leurs points d'entrée.
- [x] Création du projet, références permises.
- [x] `AppDefinition` : membres utiles, règles du générateur.
- [x] Exemple complet d'app fenêtrée (fichiers, paramètres, journal, dialogue, notification, CSS).
- [x] Exemple de commande du shell (arguments, flux, code de sortie).
- [x] Exemple de service (boucle, horloge simulée, annulation).
- [x] Table des services de l'OS et capacités associées.
- [x] Installation dans l'hôte et test unitaire d'une commande.
- [x] Compiler les exemples et exécuter le test contre `rebuild/base`.
- [x] Construire une app ou un hôte depuis un autre dépôt avec les paquets locaux et les modèles (v3/6).
- [x] Guide complet du dépôt tiers, SDK 1.0 (`v3/15`, ADR 0114) : [`building-on-hackeros.md`](building-on-hackeros.md).
- [x] Sauvegarde de la machine et erreurs `NoSpace`/`FileTooLarge` (`v3/7-storage-backup`).
- [x] Traduire son app : ressources, `GetLocalizer<T>`, `AppManifest.fr.resx` (`v3/12`, ADR 0104).
- [x] Calculs lourds hors de la fenêtre avec `HackBackground` (`exec/5`, ADR 0128 et 0140).
- [x] SDK 1.2 : `HackWireContext` d'un service en worker, `OomScoreAdjust`, limites de processus, `TooManyOpenFiles` (`exec/10-final`).
- [x] SDK 2.0 : modèles 2.0.0, `HackFileView` et `HackFileOperations` dans la table des services (`desk/6-sdk-2`, ADR 0161) ; migration d'une app 1.x dans [`sdk-packages.md`](sdk-packages.md#sdk-20).
