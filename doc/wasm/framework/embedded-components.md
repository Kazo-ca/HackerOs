# Composants intégrables

Une app peut afficher dans sa fenêtre un composant qu'une autre app a
enregistré auprès de l'OS, sans référencer son assemblage, comme Kate affiche
la KPart de Konsole. Décision : [ADR 0063](../dev/adr/0063-embeddable-app-components.md).
Le même mécanisme porte les contrôles et dialogues que l'OS fournit à toutes
les apps (le SDK ne contient que leurs contrats) :
[ADR 0148](../dev/adr/0148-sdk-contracts-and-os-provided-controls.md), ajusté par
l'[ADR 0153](../dev/adr/0153-contract-host-implementation-adjustments.md).

## Enregistrer un composant (app fournisseur)

```csharp
[AppManifest]
public sealed class TerminalApp : AppDefinition
{
    // ...
    public override IReadOnlyList<AppComponent> Components =>
        [new(HackComponentContracts.Terminal, typeof(TerminalEmbed))];
}
```

```razor
@inherits HackEmbeddableComponent
<TerminalPanel @ref="_panel" AppContext="HostContext" ... />

@code {
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await ControllerReady.InvokeAsync(_panel);
    }
}
```

Règles (vérifiées par `AppManifestValidator`) : app `Window`, contrat en mots
minuscules séparés par des points, un contrat par app, type dans l'assemblage
du point d'entrée. Le composant dérive de `HackEmbeddableComponent` et reçoit
`Options` et `ControllerReady` ; `HostContext` est le contexte de la fenêtre
hôte.

## Embarquer un composant (app hôte)

```razor
<HackEmbeddedComponent TController="IHackTerminal"
                       Contract="@HackComponentContracts.Terminal"
                       Options="_options"
                       ControllerChanged="terminal => _terminal = terminal">
    <Unavailable Context="reason"><p>@reason</p></Unavailable>
</HackEmbeddedComponent>
```

Garder la même instance d'`Options` d'un rendu à l'autre : une nouvelle
instance est transmise au composant. Le contrôleur reste valable tant que
`HackEmbeddedComponent` est rendu.

## Composant typé (contrat fourni par l'OS)

Pour un contrat de l'OS, le SDK donne un composant typé que l'app utilise
comme n'importe quel composant Blazor. Il dérive de
`HackContractComponent<TController>` et recopie ses paramètres par nom
(forme réduite ; le vrai `HackFileView` est décrit plus bas) :

```csharp
public sealed class HackFileView : HackContractComponent<IHackFileView>
{
    [Parameter] public string? Path { get; set; }
    [Parameter] public EventCallback<string> Opened { get; set; }

    protected override string Contract => HackComponentContracts.FileView;

    protected override void AddProviderParameters(IDictionary<string, object?> parameters)
    {
        parameters[nameof(Path)] = Path;
        parameters[nameof(Opened)] = Opened;
    }
}
```

Le fournisseur (`HackEmbeddableComponent`) déclare les mêmes noms en
`[Parameter]`. Un paramètre qu'il ne connaît pas encore (app compilée avec un
SDK plus récent) est rangé dans `UnmatchedParameters` au lieu de faire
échouer le rendu ; il ne doit donc pas déclarer son propre
`CaptureUnmatchedValues`. La base expose `ControllerChanged`, `Unavailable`,
`Controller` et le crochet `OnControllerChangedAsync`, et ne transmet un
nouveau jeu de paramètres que si une valeur a changé. En dessous,
`HackEmbeddedComponent<T>` reçoit ces valeurs par son paramètre `Parameters`
(`Options` et `ControllerReady` gardent la priorité).

**Droits.** Le fournisseur est rendu dans la fenêtre de l'hôte, avec
l'`AppContext` de l'hôte ; les callbacks qu'il invoque (`Opened`…)
s'exécutent dans le contexte de l'hôte. Une app ne voit pas un fichier de plus
en affichant le contrôle d'une autre.

## Registre et fournisseurs

- **Apps.** Le catalogue construit le registre au démarrage à partir des
  manifestes (`AppCatalog.ComponentProviders`,
  `GetComponentProviders(contract)`) : apps système d'abord, puis par
  identifiant. `HackComponentHost` saute les apps désinstallées et charge
  l'assemblage du fournisseur au premier usage.
- **Choix de l'administrateur** (ADR 0156, ADR 0158). Pour
  `hackeros.file-view` et `hackeros.file-dialogs`, `HackComponentHost` lit
  `/etc/hackeros/components.config` (`IComponentProviderSelection`, relu à
  chaque résolution) et essaie d'abord l'app choisie ; les autres restent des
  replis, l'explorateur en premier. Si l'app choisie est désactivée pour
  l'utilisateur ou ne se charge pas, le suivant prend le relais et
  `component.fallback` est écrit en avertissement dans `/var/log/syslog`
  (source `components`), une fois par session. Sans ligne, l'explorateur
  s'applique.

  ```ini
  [providers]
  hackeros.file-view=com.example.files
  hackeros.file-dialogs=com.example.files
  ```

  Le choix se fait dans Paramètres ▸ Composants de l'OS
  (`HackComponentProviders`, capacité `apps.manage`) : une app non système
  passe par la fenêtre d'approbation des capacités dangereuses, avec la phrase
  de risque « Le code de cette app s'exécutera dans toutes les applications
  qui affichent des fichiers, avec leurs droits. » ; une app système ou le
  retour à l'explorateur demandent seulement un administrateur. Chaque
  décision va dans `/var/log/auth.log` (`component.provider`). Une app
  désinstallée pour la machine perd sa ligne.
- **Plateforme.** La plateforme inscrit ses propres fournisseurs dans le
  conteneur de services (`services.AddSingleton(new HackPlatformComponent(contrat, typeof(Composant)))`) ;
  ils passent avant toute app, sans chargement, et leur `ProviderAppId` vaut
  `HackComponentResolution.PlatformProviderId` (`"platform"`).
- **Qui peut fournir** (`AppComponentContracts.GetProviderScope`) :

| Contrat | Fournisseur admis | Refus |
| --- | --- | --- |
| `hackeros.terminal` | toute app fenêtrée | — |
| `hackeros.file-view`, `hackeros.file-dialogs` | toute app fenêtrée (`AdministratorSelected`) ; l'administrateur choisit celle en service, l'explorateur par défaut (ADR 0156) | — (`HOSAPP0025` est retiré, ADR 0158 §1) |
| `hackeros.context-menu` | plateforme | `manifest.component.contract.platform` (`HOSAPP0009` au build de l'app) |

- **Indisponible.** Un contrat de l'OS (fichiers ou menu) sans aucun
  fournisseur qui se charge est une erreur de l'OS : `component.unavailable` est écrit dans `/var/log/syslog`
  (source `components`). Un contrat ouvert sans fournisseur est seulement
  montré à l'utilisateur (`Unavailable`).

## Contrats

| Contrat | Contrôleur | Options | Fournisseur |
| --- | --- | --- | --- |
| `hackeros.terminal` (`HackComponentContracts.Terminal`) | `IHackTerminal` | `HackTerminalOptions` (`WorkingDirectory`, `ShowBanner`, `FocusOnStart`) | Terminal (`TerminalEmbed`) |
| `hackeros.file-view` (`HackComponentContracts.FileView`) | `IHackFileView`, composant `HackFileView` (ADR 0155) | paramètres de `HackFileView` | Explorateur de fichiers (`ExplorerFileView`) |
| `hackeros.file-dialogs` (`HackComponentContracts.FileDialogs`) | aucun : ouverts par `IDialogService.OpenWithAsync`, `ShowFilePropertiesAsync`, `ResolveFileConflictAsync` (`HackFileDialogs`, ADR 0155 §5) | paramètres `Dialog`, `Request`, `Completed` | Explorateur de fichiers (`ExplorerFileDialogs`) |
| `hackeros.context-menu` (`HackComponentContracts.ContextMenu`) | `IHackContextMenu`, composant `HackContextMenu` ([`context-menu.md`](context-menu.md)) | — | Plateforme (`PlatformContextMenu`, qui inscrit le menu ; `ContextMenuOverlay` le dessine, ADR 0154) |

### `HackFileView` et `IHackFileView`

```razor
<HackFileView InitialDirectory="@home"
              Mode="HackFileViewMode.Icons"
              Features="HackFileViewFeatures.All & ~HackFileViewFeatures.Delete"
              Opening="OnOpening"
              ContextMenuOpening="OnMenu"
              ControllerChanged="files => _files = files" />
```

| Paramètre | Rôle |
| --- | --- |
| `InitialDirectory`, `Mode`/`ModeChanged`, `AllowMultiSelect`, `Filter` | Dossier, disposition (`Details`, `Icons`, `Tree`, `Desktop`), sélection, filtre (rappeler `RefreshFilter`). |
| `Features` | Ce que le contrôle fait seul (`Clipboard`, `Delete`, `Rename`, `CreateFolder`, `DragDrop`, `OpenWith`, `Properties`) ; une fonction retirée disparaît du menu et sa touche ne fait rien. |
| `SelectedResource` | Poignée d'un dossier choisi dans le dialogue de l'OS : le contrôle liste avec elle. |
| `Opening` | Avant l'ouverture d'un élément ; `Handled = true` laisse l'hôte l'ouvrir lui-même. |
| `Navigated`, `SelectionChanged`, `Renamed`, `Deleted`, `ErrorOccurred` | Ce qui s'est passé ; sans `ErrorOccurred`, les erreurs deviennent des notifications. |
| `DesktopOptions`, `DesktopPositions`, `DesktopPositionsChanged` | Mode `Desktop` seulement (ADR 0160 §2) : options de `[view]`, case de chaque élément (`::home`, `::trash` pour les icônes système), positions à enregistrer après un déplacement ou un élément nouveau. L'hôte garde `desktop.config`, le fournisseur place avec `HackDesktopLayout`. Voir [`desktop.md`](../dev/desktop.md). |
| `ContextMenuOpening` | Après la construction du menu du plan §7 : `Scope`, `Items`, `Directory`, `Menu` (trouver, cacher, désactiver, ajouter un groupe), `Cancel`. Les entrées inscrites par les apps sont ajoutées **après** par l'OS (ADR 0159 §4). |

`IHackFileView` : `CurrentDirectory`, `Items`, `SelectedItems`, `CanPaste`,
`IsBusy`, `StateChanged`, `NavigateAsync`, `RefreshAsync`, `RevealAsync`,
`ClearSelection`, `RefreshFilter`, `WhenIdleAsync`, et
`CanExecute`/`ExecuteAsync(commande)` dont les commandes sont les
identifiants de `HackFileMenuItems` (`open`, `cut`, `paste`, `rename`,
`delete`, `properties`…). Tout s'exécute avec les droits de l'hôte.

Depuis l'ADR 0165, la fenêtre de l'explorateur combine sa recherche avec une
commande **Afficher les fichiers et dossiers cachés**, désactivée par défaut :
les noms commençant par un point sont masqués. Ce filtre s'applique aussi aux
descendants déjà chargés de l'arbre et à leur navigation clavier. La configuration
de l'accueil mobile est décrite dans [mobile-home-screen.md](../dev/mobile-home-screen.md).

Un fournisseur du contrôle (l'explorateur, ou l'app tierce choisie par
l'administrateur) **pose `HackMenuDefinition.FileMenu`** (`HackFileMenuContext` :
portée, éléments, dossier affiché) sur chaque menu de fichiers qu'il ouvre :
c'est ce qui permet à l'OS d'y ajouter les entrées des apps (ADR 0159 §4, voir
[`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4)).

### `IHackTerminal`

| Membre | Rôle |
| --- | --- |
| `RunCommandAsync(ligne, jeton)` | Affiche la ligne à l'invite, l'exécute après les touches déjà tapées, renvoie `HackTerminalCommandResult(CommandLine, ExitStatus, Output)`. Le jeton interrompt la commande (statut 130). |
| `SetWorkingDirectoryAsync(chemin)` | `cd` visible dans le terminal. |
| `SendInputAsync(texte)` | Octets du clavier (`\r` = Entrée, `\u0003` = Ctrl+C). |
| `ReadOutput(max)` | Texte affiché récemment, sans séquences de contrôle (256 Kio gardés). |
| `ClearAsync()`, `FocusAsync()` | Effacer l'écran, prendre le focus. |
| `WorkingDirectory`, `Title`, `IsRunning`, `IsBusy` | État. |
| `OutputReceived`, `CommandCompleted`, `StateChanged`, `Exited` | Événements sur le thread de rendu. |

Le shell est un enfant du processus de l'hôte : l'hôte doit demander
`shell.host`.

## Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `AppComponent`, `AppComponentManifest`, `AppManifest.Components` | `HackerOs.App.Abstractions` | Déclaration et manifeste (`components`, omis si vide). |
| `HackComponentContracts`, `IHackTerminal`, `HackTerminalOptions`, `HackTerminalCommandResult` | `HackerOs.AppSdk` (`.Components`) | Contrats. |
| `AppComponentContracts`, `AppComponentProviderScope` | `HackerOs.App.Abstractions` | Contrats connus de l'OS et qui peut les fournir (ADR 0148 §3.3, ADR 0156). |
| `IHackComponentHost`, `HackComponentResolution`, `HackEmbeddableComponent`, `HackEmbeddedComponent<T>`, `HackContractComponent<T>` | `HackerOs.AppSdk.Blazor` (`.Components`) | Côté Blazor du SDK. |
| `AppCatalog.ComponentProviders`, `AppComponentProvider` | `HackerOs.Platform.Core` | Registre des fournisseurs lu au démarrage. |
| `HackComponentHost`, `HackPlatformComponent` | `HackerOs.Platform.Blazor` (`.Components`) | Fournisseurs de la plateforme d'abord, puis le choix de l'administrateur (`Selection`), puis le premier fournisseur installé qui se charge (`AppLifecycleOrchestrator.EnsureDescriptorAsync`) ; vérifie le type, écrit `component.fallback` (`ReportWarning`) et `component.unavailable`. |
| `ComponentProviderService`, `ComponentProvidersDocument`, `IComponentProviderSelection` | `HackerOs.Platform.Core` (`.ComponentProviders`) | `/etc/hackeros/components.config` : lecture, approbation, écriture, audit, retrait à la désinstallation (ADR 0156, ADR 0158). |
| `HackComponentProviders`, `IAppComponentProviderAdministrationGateway`, `HackComponentContractInfo`, `HackComponentProviderInfo` | `HackerOs.AppSdk` (`.Administration`) | Choix du fournisseur pour Paramètres (`apps.manage`). |
| `OsComponentsPanel` | `HackerOs.Apps.Settings` | Onglet « Composants de l'OS ». |
| `TerminalEmbed` | `HackerOs.Apps.Terminal` | Fournisseur de `hackeros.terminal`. |

## Tâches

- [x] `Components` dans `AppDefinition`, le mappeur, `AppManifest`, le schéma,
  le sérialiseur, le canonique de test et le validateur.
- [x] Contrats `IHackTerminal` et `HackTerminalOptions`.
- [x] `HackEmbeddableComponent`, `HackEmbeddedComponent<T>`,
  `IHackComponentHost`.
- [x] `HackComponentHost` et `EnsureDescriptorAsync` (chargement paresseux).
- [x] `TerminalPanel` implémente `IHackTerminal` (file d'entrée, transcription,
  résultat des commandes) ; `TerminalEmbed` enregistré par l'app Terminal.
- [x] L'éditeur embarque le terminal par contrat dans son dock.
- [x] Tests : manifeste, validateur, mappeur, registre, nettoyage des
  séquences.
- [x] Chantier 1 du plan du bureau (ADR 0148, ADR 0153) : composant typé
  `HackContractComponent<T>` et `Parameters`, `UnmatchedParameters`, registre
  dans `AppCatalog`, contrats réservés (`HOSAPP0025`,
  `catalog.component.reserved`, `manifest.component.contract.platform`),
  `HackPlatformComponent`, `component.unavailable` au journal ; tests du
  validateur, du catalogue, de l'agrégation, du registre et du composant typé.
- [x] Chantier 3c du plan du bureau (ADR 0156, ADR 0158) : contrats du
  fichier ouverts à toute app fenêtrée (`HOSAPP0025` retiré),
  `/etc/hackeros/components.config`, choix de l'administrateur essayé en
  premier, repli `component.fallback`, `HackComponentProviders` et onglet
  « Composants de l'OS » avec la fenêtre d'approbation, `component.provider`
  dans `auth.log`, ligne retirée à la désinstallation pour la machine ; tests
  du service, de la passerelle, du registre, du catalogue, du validateur et de
  l'agrégation.
- [x] Chantier 3 du plan du bureau (ADR 0155) : `HackFileView`, `IHackFileView`
  et les dialogues fournis, `ExplorerFileView` et `ExplorerFileDialogs`
  inscrits par l'explorateur ; test de la transmission de tous les paramètres.
