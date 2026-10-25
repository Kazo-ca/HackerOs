> [English version](../../../en/wasm/dev/resource-accounting.md)

# Mesure des ressources optionnelle : interrupteurs et crochet de changement de contexte

Mise en œuvre du chantier `rebuild/5-accounting-switches` de la phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)) :
[ADR 0066](adr/0066-optional-resource-accounting.md), et le crochet commun de
l'[ADR 0067](adr/0067-measured-cpu-time.md) §1 que les chantiers
`5-cpu-accounting` et `5-memory-accounting` utiliseront.

## Rôle

- Choisir **au build** si le code de mesure du CPU et de la mémoire est livré,
  et **au démarrage** (`appsettings.json`) s'il est branché.
- Installer, quand au moins une mesure est active, le **crochet de changement
  de contexte** : le rappel de l'`AsyncLocal` ambiant de
  `HackExecutionContext`, qui voit chaque processus entrer et sortir du
  thread, y compris dans les continuations async.
- Coupé (ou pas encore implémenté), revenir **exactement** au simulateur
  actuel, avec un indicateur : `CpuSource:` / `MemSource:` dans
  `/proc/<pid>/status`, mention « estimated » dans le gestionnaire de tâches.

Les deux mesures sont faites. Le CPU par `MeasuredCpuTimeAccountant` (chantier
`5-cpu-accounting`, [`cpu-accounting.md`](cpu-accounting.md)), la mémoire
depuis le chantier `5-memory-accounting`
([`memory-accounting.md`](memory-accounting.md)) : son `AllocationTracker` est
un observateur du crochet, et `MeasuredMemoryAccountant` remplace le comptable
nul.

## Architecture

| Élément | Projet | Rôle |
|---|---|---|
| `ICpuTimeAccountant`, `IMemoryAccountant`, `ResourceMeasurementSource` | `Shared/HackerOs.Simulation.Abstractions` (`Processes/ResourceAccountingContracts.cs`) | contrats lus par `/proc` ; `TryGet…` faux = valeur simulée |
| `IExecutionContextSwitchObserver`, `ExecutionContextSwitch` | idem | contrat d'observateur du crochet : processus quitté, processus entré, changement fait par le runtime (`ThreadContextChanged`), et depuis l'ADR 0067 le mode de chacun (`FromKernel`, `ToKernel`) |
| `IExecutionModeSwitchObserver` | idem | changements de mode (entrée ou sortie d'une passerelle de l'OS) sans changement de processus ; facultatif (ADR 0067, [`cpu-accounting.md`](cpu-accounting.md)) |
| `NullCpuTimeAccountant`, `NullMemoryAccountant` | `Platform/HackerOs.Platform.Core` (`Processes/Accounting/`) | implémentations nulles ; Platform Core ne dépend jamais du projet de mesure |
| `ResourceAccountingPlan`, `ResourceAccountingSettings`, `ResourceAccountingBuildSupport` | idem | décision prise une fois : build × réglage → mesures actives, crochet requis, lignes pour syslog |
| `HackExecutionContext` (`InstallContextSwitchHook`, interne) | `Shared/HackerOs.AppSdk` | remplace le frame ambiant par un `AsyncLocal<T>(valueChangedHandler)` ; visible seulement du projet de mesure (`InternalsVisibleTo`) |
| `ContextSwitchHook`, `ContextSwitchDispatcher`, `ResourceAccountingFeatures` | `Platform/HackerOs.Platform.ResourceAccounting` (nouveau) | installation du crochet, diffusion aux mesures (changements de processus, et de mode aux observateurs qui les veulent), interrupteurs `[FeatureSwitchDefinition]` |
| `MeasuredCpuTimeAccountant` | idem | mesure du CPU (ADR 0067), inscrite sur le diffuseur quand `plan.CpuActive` |
| `ResourceAccountingServiceCollectionExtensions` | `OS/HackerOs.Ecosystem.Host` | lecture de `HackerOs:ResourceAccounting`, composition, `#if HACKEROS_RESOURCE_ACCOUNTING` |
| `build/HackerOs.ResourceAccounting.targets` | `build/` | propriétés MSBuild, `RuntimeHostConfigurationOption`, référence conditionnelle |

### Le crochet

```
HackExecutionContext.Enter(P) ──┐
continuation restaurée (await) ─┼─► AsyncLocal<frame>(OnFrameChanged)
fin de continuation, minuterie ─┘         │ processus différent ?
                                          ▼
                          ContextSwitchDispatcher.OnContextSwitch(in switch)
                                  │                 │
                         mesure CPU (0067)   mesure mémoire (0068)
```

- Sans mesure active, le frame reste un `AsyncLocal` ordinaire : aucun rappel,
  aucun test « activé ? » dans le chemin chaud.
- Le rappel ne signale que les vrais changements de processus (pas un frame
  imbriqué du même processus, pas le code de l'OS qui reprend du code de
  l'OS). Il ne lève jamais : une exception dans la restauration d'un
  `ExecutionContext` arrêterait le runtime ; un observateur qui lève est
  ignoré pour ce changement.
- Remplacer l'`AsyncLocal` ferait perdre leur contexte aux flots qui ont
  capturé l'ancien : l'installation est donc refusée si une portée est ouverte
  (compteur de portées vivantes) ou si un crochet existe déjà. L'OS l'installe
  à la composition des services, avant tout code d'app, et ne la retire jamais.
- Le diffuseur copie son tableau d'observateurs à l'inscription : la lecture
  dans le chemin chaud se fait sans verrou ni allocation.

## Utilisation

### Au build

```xml
<!-- Directory.Build.props, ou en ligne de commande : -p:HackerOsCpuAccounting=false -->
<HackerOsCpuAccounting>false</HackerOsCpuAccounting>
<HackerOsMemoryAccounting>false</HackerOsMemoryAccounting>
```

Les deux valent `true` par défaut. Les trois hôtes importent
`build/HackerOs.ResourceAccounting.targets` à la fin de leur `.csproj`.

| Propriétés | Effet |
|---|---|
| une au moins à `true` | référence vers `HackerOs.Platform.ResourceAccounting`, constante `HACKEROS_RESOURCE_ACCOUNTING` ; chaque propriété devient l'option d'exécution `HackerOs.ResourceAccounting.{Cpu,Memory}.IsSupported` |
| les deux à `false` | pas de référence : le code de mesure n'est **pas dans le binaire** ; la composition compile sa branche `#else` |

La composition vit dans `HackerOs.Ecosystem.Host` (qui importe aussi ces cibles),
que l'Ecosystem, `test/test` et `HackerOs.Server` référencent. Le paquet
`HackerOs.Ecosystem.Host` est produit avec les deux mesures compilées ; un hôte
d'un autre dépôt les coupe à l'exécution (`appsettings.json` ou ses propres
options `RuntimeHostConfigurationOption`). Pour retirer le code de **tout** le graphe,
passer les propriétés en ligne de commande ou dans un `Directory.Build.props`.
Posées seulement dans le `.csproj` de `test/test` ou du serveur, elles coupent
l'option d'exécution de cet hôte (la mesure est ignorée au démarrage, avec la
ligne syslog) mais la DLL arrive encore par `HackerOs.Ecosystem.Host`.

### Au démarrage

```json
{
  "HackerOs": {
    "ResourceAccounting": {
      "Cpu":    { "Enabled": true, "UnresponsiveThresholdMs": 5000 },
      "Memory": { "Enabled": true }
    }
  }
}
```

| Hôte | Fichier | Défaut livré |
|---|---|---|
| `OS/HackerOs.Ecosystem` | `wwwroot/appsettings.json` (lu par `WebAssemblyHostBuilder`, servi aussi à l'îlot WASM de `test/test`) | les deux activées |
| `test/test` | `appsettings.json` (composition côté serveur, pré-rendu) | les deux activées |
| `Server/HackerOs.Server` | aucun : il sert l'OS de l'Ecosystem et son `wwwroot/appsettings.json` (ADR 0130) | comme l'Ecosystem (activées) |

Chaque hôte appelle, avant `AddHackerOsEcosystem` :

```csharp
builder.Services.AddHackerOsResourceAccounting(builder.Configuration, BuildKnownLazyApps.Images);
```

(`AddHackerOsBrowserHost` le fait pour un hôte WebAssembly, avec les images de son
`HostAppCatalog` ; voir [`sdk-packages.md`](../framework/sdk-packages.md).)

- La section est lue **une fois** ; la changer demande de recharger la page.
- Une valeur absente prend son défaut ; une valeur illisible aussi, avec une
  ligne d'avertissement dans syslog.
- Une mesure coupée au build l'emporte : `Enabled: true` est ignoré et
  `/var/log/syslog` le dit (catégorie `resource-accounting`, écrit par
  `EcosystemBootCoordinator` dès que le journal est attaché), suivi d'une
  ligne de résumé, par exemple
  `resource accounting: cpu off (not built), memory on`.
- Un hôte ou un test qui n'appelle pas `AddHackerOsResourceAccounting` reçoit
  `ResourceAccountingPlan.Disabled` et les comptables nuls.

### Inscrire une mesure

La mesure du CPU est inscrite ainsi, dès la composition (avant tout code
d'app) ; la mesure de la mémoire inscrit son `AllocationTracker` de la même
façon :

```csharp
// Dans AddHackerOsResourceAccounting, quand plan.CpuActive :
MeasuredCpuTimeAccountant cpu = new(plan.Settings.UnresponsiveThreshold);
services.AddSingleton(new CpuAccountingRegistration(dispatcher.Register(cpu)));
services.AddSingleton(cpu);
services.AddSingleton<ICpuTimeAccountant>(cpu);
```

`AddHackerOsResourceAccounting` enregistre les comptables nuls (et
`NullKernelMemoryLedger`) par `TryAddSingleton` : une mesure enregistrée avant
eux les remplace. La mémoire le fait déjà (`AddMeasuredMemory`, sous
`ResourceAccountingFeatures.IsMemoryAccountingSupported`).

### Lecture

- `/proc/<pid>/status` : `Cpu` et `VmRSS` prennent la valeur mesurée quand le
  comptable répond, sinon celle du simulateur ; `CpuSource:` et `MemSource:`
  valent `measured` ou `simulated`
  ([`virtual-proc-and-dev.md`](virtual-proc-and-dev.md)). Une mémoire mesurée
  ajoute ses couches après `VmRSS`, et `/proc/meminfo` ses lignes
  ([`memory-accounting.md`](memory-accounting.md)).
- Gestionnaire de tâches : l'en-tête des colonnes CPU et Memory porte une
  étiquette `estimated` (petite, en pointillés, monospace) tant qu'une ligne
  au moins est simulée. L'app lit `HackProcess.Status` : le SDK ne change pas.

## Apps existantes

Section 5.1 du plan : le crochet compte au compositeur le code exécuté hors de
tout frame, donc tout le code des apps de `Apps/` doit tourner dans le frame de
son processus.

| Point d'entrée | Qui ouvre le frame |
|---|---|
| Commande (`TerminalAppBase.ExecuteAsync`), service (`ServiceAppBase`) | l'OS, autour de l'appel ; le frame suit les continuations, minuteries et `Task.Run` |
| Fenêtre (`WindowAppBase`) | paramètres, cycle de vie, événements, après-rendu |
| Composant enfant | `HackComponentBase` (ou `HackEmbeddableComponent`), contexte reçu en cascade de `WindowAppRenderer` |
| Dialogue MudBlazor (rendu hors de la fenêtre) | le composant du dialogue reçoit le contexte de la fenêtre en paramètre (l'ancien `FileExplorerDialogBase` et son `OwnerAppContext`) ; depuis `desk/3` (ADR 0155), les dialogues de l'explorateur sont des dialogues fournis rendus par la plateforme dans une fenêtre modale qui donne le contexte de l'app appelante en cascade (`ProvidedFileDialog`) |
| Méthode `[JSInvokable]` | la méthode elle-même, en première instruction : `using IDisposable scope = HackExecutionContext.Enter(AppContext);` |

Le rendu du balisage et la libération des composants restent hors frame : c'est
le travail du compositeur (ADR 0067 §2).

Migration faite dans ce chantier :

- vers `HackComponentBase` : `NetworkTab`, `PerformanceTab`, `DiskTab`
  (System Monitor), `CodeEditorSearchView`, `CodeEditorExplorer`,
  `CodeEditorQuickOpenPalette`, `CodeEditorCursorStatus`,
  `SimulatedPageRenderer` (Browser), `AppPermissionsPanel` et
  `InstalledAppsPanel` (Settings, qui ne prennent plus `AppContext` en
  paramètre mais le reçoivent en cascade) ;
- vers `FileExplorerDialogBase` : `FileConflictDialog`, `OpenWithDialog`,
  `FilePropertiesDialog` (retirés depuis par `desk/3`, remplacés par
  `ExplorerFileDialogs`, ADR 0155) ;
- frame ouvert en première instruction : `SystemMonitorWindow.SetWindowVisible`
  et les quatre rappels JavaScript de `CodeEditorWindow`.

`AmbientFrameSourceTests` (`HackerOs.Ecosystem.Tests`) lit toutes les sources de
`Apps/` et échoue si un composant ne dérive pas (directement ou par une base de
l'app) de `WindowAppBase`, `HackComponentBase` ou `HackEmbeddableComponent`, si
une méthode `[JSInvokable]` n'entre pas dans le frame en premier, ou si du code
utilise `ExecutionContext.SuppressFlow`, `UnsafeQueueUserWorkItem`,
`UnsafeRegister` ou `new Thread(`. Avant la migration, il signalait les treize
composants et les cinq rappels ci-dessus.

## Décisions

- **Crochet dans `HackExecutionContext`, installation réservée.** Le frame est
  privé au SDK ; seul `HackerOs.Platform.ResourceAccounting` voit les membres
  internes qui l'échangent. Une app ne peut ni installer ni observer le
  crochet.
- **Échange de l'`AsyncLocal` plutôt qu'un rappel toujours présent.** Un
  `AsyncLocal` avec rappel coûte à chaque restauration de contexte ; sans
  mesure, il n'existe pas.
- **Installation à la composition, pas au démarrage de l'OS.** C'est le seul
  moment où aucune portée n'est ouverte ; les tests de l'Ecosystem passent un
  installateur factice pour couvrir toutes les combinaisons sans toucher au
  crochet du processus de test.
- **Source par processus.** `TryGetCpuUsage` / `TryGetProcessMemory` répondent
  processus par processus : les processus simulés, qui n'exécutent
  pas de code, resteront simulés même mesure active (ADR 0066 §4).
- **Lecture manuelle de la configuration** (pas de liaison par réflexion) :
  compatible avec l'analyse de trimming de la solution.

## Tests

| Suite | Couvre |
|---|---|
| `HackerOs.Platform.ResourceAccounting.Tests` | crochet : entrée et sortie, continuations (`ThreadContextChanged`), `Task.Run`, processus imbriqués, même processus ignoré, double installation et portée ouverte refusées, observateur qui lève, retrait ; diffuseur ; interrupteurs indépendants. Parallélisme désactivé (crochet global). |
| `HackerOs.Platform.Core.Tests` (`ResourceAccountingPlanTests`) | les 16 combinaisons build × réglage, lignes syslog, comptables nuls |
| `HackerOs.Platform.Core.Tests` (`ProcFileSystemProviderTests`) | format de `status`, retour au simulateur, sources mesurées indépendantes |
| `HackerOs.Ecosystem.Tests` (`ResourceAccountingCompositionTests`) | crochet installé seulement si une mesure est active (16 combinaisons), aucun crochet par le vrai chemin quand tout est coupé, lecture de la section, `appsettings.json` livrés |
| `HackerOs.Apps.SystemMonitor.Tests` | mention « estimated » par colonne |
| `HackerOs.Ecosystem.Tests` (`AmbientFrameSourceTests`) | tout le code des apps de `Apps/` dans un frame ambiant (composants, rappels JavaScript, pas d'évasion du contexte), avec un contrôle que l'analyse détecte bien une faute |

Vérifié à la main : un build de `HackerOs.Ecosystem` avec les deux propriétés
à `false` ne contient aucun fichier `HackerOs.Platform.ResourceAccounting*` et
son `runtimeconfig.json` porte les deux options à `false`.

## Liste des tâches

- [x] Contrats `ICpuTimeAccountant`, `IMemoryAccountant`, `ResourceMeasurementSource`, `IExecutionContextSwitchObserver` (Simulation Abstractions).
- [x] Implémentations nulles et `ResourceAccountingPlan` (Platform Core).
- [x] Crochet `AsyncLocal<T>(valueChangedHandler)` installable dans `HackExecutionContext`, refusé si une portée est ouverte.
- [x] Projet `HackerOs.Platform.ResourceAccounting` : interrupteurs, diffuseur, installation.
- [x] `build/HackerOs.ResourceAccounting.targets`, import dans les trois hôtes, référence conditionnelle, `RuntimeHostConfigurationOption`.
- [x] `appsettings.json` des trois hôtes (créé pour l'Ecosystem, coupé pour le serveur), lecture à la composition, lignes syslog.
- [x] `CpuSource` / `MemSource` dans `/proc/<pid>/status`, mention « estimated » dans le gestionnaire de tâches.
- [x] Tests des combinaisons, aucun crochet quand tout est coupé, observateur de test du crochet.
- [x] Documentation et index.
- [x] Apps existantes (plan §5.1) : composants de `Apps/` sur `HackComponentBase`, dialogues de l'explorateur sur `FileExplorerDialogBase`, rappels `[JSInvokable]` dans le frame, test `AmbientFrameSourceTests`.
