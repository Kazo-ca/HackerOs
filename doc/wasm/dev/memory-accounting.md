# Mémoire mesurée par processus, en couches

Mise en œuvre du chantier `rebuild/5-memory-accounting` de la phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)) :
[ADR 0068](adr/0068-measured-process-memory.md), branchée sur les interrupteurs
et le crochet de changement de contexte de l'[ADR 0066](adr/0066-optional-resource-accounting.md)
([`resource-accounting.md`](resource-accounting.md)).

## Rôle

Toutes les apps partagent le même tas .NET : le runtime ne dit pas quelle part
en revient à quelle app. La mesure reconstitue la mémoire de chaque processus
en quatre couches, comme `/proc/<pid>/status` sous Linux, **sans modifier le
code des apps** :

| Couche | Source | Précision | Champ de `/proc/<pid>/status` |
|---|---|---|---|
| Image | taille des assemblies de l'app, mesurée par le build de l'hôte | exacte | `VmExe` (l'assembly de l'app), `RssFile` (+ assemblies partagées) |
| Ressources de l'OS | octets que l'OS tient pour le processus : tampons de `HackFileStream`, presse-papiers, tubes, sockets en vol, coût fixe par fenêtre | exacte | `VmData` (part noyau) |
| Allocations | `GC.GetAllocatedBytesForCurrentThread()` lu à chaque changement de contexte | exacte | `HackAllocBytes` |
| Résidente | tas réel réparti après chaque GC au prorata des allocations récentes, avec décroissance exponentielle | estimation | `RssAnon` |

`VmRSS = RssAnon + RssFile + VmData`. La mesure ne limite rien par elle-même :
la limite `rss` par processus vient de l'ADR 0125
([`process-limits.md`](process-limits.md)) et l'OOM killer, **désactivé par
défaut**, de l'ADR 0126 (qui remplace l'ADR 0068 §3) et de l'ADR 0142
([`oom-killer.md`](oom-killer.md)) ; il n'agit que si la mémoire est mesurée.

Mesure coupée (au build par `HackerOsMemoryAccounting=false`, ou par
`HackerOs:ResourceAccounting:Memory:Enabled=false`) : comportement exact
d'avant la phase 5 (`VmRSS` simulé, aucune ligne de couche, `meminfo` à trois
lignes, `MemSource: simulated`).

## Architecture

| Élément | Projet | Rôle |
|---|---|---|
| `IMemoryAccountant` (`TryGetProcessMemory`, `TryGetSystemMemory`), `ProcessMemoryUsage`, `SystemMemoryUsage` | `Shared/HackerOs.Simulation.Abstractions` (`Processes/`) | contrat lu par `/proc` ; `false` = valeur simulée |
| `IKernelMemoryLedger`, `KernelMemoryCharge`, `KernelMemoryKind` | idem (`Processes/MemoryAccountingContracts.cs`) | registre de la mémoire tenue par l'OS pour un processus |
| `NullMemoryAccountant`, `NullKernelMemoryLedger` | `Platform/HackerOs.Platform.Core` (`Processes/Accounting/`) | mesure coupée : rien n'est mesuré, aucune charge ne coûte |
| `AppImage`, `AppImageAssembly`, `BuiltInApplication.Image` | `Shared/HackerOs.App.Abstractions` | image d'une app dans `BuiltInApplications.json` |
| `AppImageInspector` | `Tools/HackerOs.Tools.ManifestGenerator` | lit les références de l'assembly (métadonnées, sans charger de code) et mesure les fichiers |
| `HostAppCatalog.Images` (`BuildKnownLazyApps.Images` dans l'Ecosystem) | `OS/HackerOs.Ecosystem.Host` | images du catalogue de l'hôte, par id d'app |
| `AllocationTracker` | `Platform/HackerOs.Platform.ResourceAccounting` (`Memory/`) | couche allocations : observateur du crochet de changement de contexte |
| `KernelMemoryLedger` | idem | couche ressources de l'OS |
| `MeasuredMemoryAccountant`, `MemoryAccountingConstants` | idem | les quatre couches, la répartition du tas, les constantes |
| `IManagedHeapProbe`, `GcManagedHeapProbe` | idem | ce qui est lu du GC (remplaçable dans les tests) |

### Image

Le build de l'hôte mesure l'image de chaque app pendant l'agrégation
(`CollectHackerOsBuiltInApplications`, [`app-model.md`](../framework/app-model.md)) :

- `GetHackerOsAppManifests` renvoie aussi le chemin de l'assembly de l'app
  (`AssemblyPath`) et sa liste de dépendances (`DependenciesFile`, le fichier
  que `GenerateHackerOsAppManifests` écrit déjà) ;
- `AppImageInspector` prend la taille de l'assembly de l'app (non partagée),
  suit ses références, directes ou non, et mesure celles qu'elle trouve dans
  la liste (partagées) ;
- le runtime et le framework (`System.*`, `Microsoft.*`, `netstandard`,
  `mscorlib`) ne comptent pas : ils sont l'OS lui-même, comme le noyau et la
  libc que partagent tous les processus. Les assemblies de référence (dossier
  `ref`) sont ignorées.

Plusieurs apps dans une même assembly (les commandes de `HackerOs.Commands`)
ont chacune cette assembly en `VmExe`, comme chaque applet BusyBox mappe le
binaire BusyBox. `/proc/meminfo` compte chaque assembly **une fois**
(`Mapped`), quel que soit le nombre de processus qui la mappent.

Une app sans image connue (installée au runtime) n'a pas de couche image.

### Ressources de l'OS

Un service de l'OS qui tient de la mémoire pour un processus ouvre une
`KernelMemoryCharge` sur le registre, la redimensionne quand son tampon change
et la libère à la fin :

| Objet | Qui charge | Taille | Chargé à |
|---|---|---|---|
| Tampon d'écriture de `HackFileStream` | le flux, par `IAppFileSystemGateway.ChargeStreamBuffer()` | capacité du `MemoryStream` | processus de l'app ; libéré à la fermeture du flux |
| Presse-papiers | `SystemClipboard`, par `AppClipboardGateway` | 2 octets par caractère (types MIME et contenus) | processus qui a copié ; libéré quand le contenu est remplacé ou effacé ; rien pour l'OS ou l'hôte |
| Tube du shell | `ShellPipe`, créé par `ShellInterpreter` | 2 octets par caractère en attente | processus du shell qui a créé le tube ; libéré à la fermeture de la lecture ou quand l'écriture est fermée et le tube vidé |
| Socket | `AppNetworkGateway`, pour chaque appel | `SocketBufferBytes` = 144 Kio (`tcp_rmem` 128 Kio + `tcp_wmem` 16 Kio) | processus de l'app, le temps de l'appel |
| Fenêtre | `MeasuredMemoryAccountant` (lecture directe) | `WindowBytes` = 512 Kio | chaque processus d'app à fenêtre |

`IAppFileSystemGateway.ChargeStreamBuffer()` est une méthode d'interface par
défaut qui rend `KernelMemoryCharge.None` : les doubles de test et les
passerelles existantes ne changent pas.

### Allocations

`AllocationTracker` est inscrit sur le `ContextSwitchDispatcher` dès la
composition. À chaque changement de contexte, il lit le compteur
d'allocations du thread et attribue la différence depuis la lecture
précédente au processus qui vient de quitter le thread (`From`) ; sans
processus (rendu, compositeur, services de l'OS), elle va à l'OS. La dernière
lecture et le processus en cours sont gardés par thread : avant chaque lecture
de `/proc`, la tranche en cours est attribuée (`FlushCurrentThread`), si bien
qu'un processus qui lit son propre `/proc/self/status` y voit déjà ses
allocations, comme une tâche en cours sous Linux. Le chemin chaud est sans verrou et, une fois le
compteur du processus créé, sans allocation.

### Résidente

À chaque lecture (`status`, `meminfo`), `MeasuredMemoryAccountant` :

1. vieillit le poids de chaque processus vivant (et de l'OS) de
   `0,5^(Δt / ResidentHalfLife)` et lui ajoute ce qu'il a alloué depuis la
   lecture précédente ;
2. oublie les processus morts (allocations et charges) ;
3. si le nombre de GC de génération 0 a changé (WebAssembly n'a pas
   `GC.RegisterForFullGCNotification`), ou à la première lecture, reprend la
   taille du tas (`GC.GetGCMemoryInfo().HeapSizeBytes`, ou
   `GC.GetTotalMemory(false)` si le runtime répond zéro), en retire la part
   noyau déjà nommée, et répartit le reste entre les processus et l'OS au
   prorata de leurs poids. La part de l'OS va dans `Slab` de `meminfo`.

Entre deux GC la répartition ne bouge pas : un processus lancé depuis le
dernier GC n'a pas encore de `RssAnon`.

### Mesuré ou simulé

Un processus est mesuré si le catalogue de l'hôte connaît son image (toutes
les apps intégrées) ou s'il a exécuté du code sous son frame (le shell, une
app installée au runtime). Les autres (processus simulés sans code) restent
simulés, comme le veut l'ADR 0066 §4.

Un processus dans un Web Worker (ADR 0070) reçoit en plus, en `RssAnon`, sa part
de la mémoire du runtime de son worker (répartie entre les processus du
worker) et ses allocations mesurées dans le worker ; `/proc/meminfo` compte
le runtime de chaque worker. Voir [`worker-processes.md`](worker-processes.md).
Quand la page est isolée (ADR 0131, 0143 §6), la mémoire du runtime d'un worker
vient de `performance.measureUserAgentSpecificMemory()` (toutes les 20 s,
valable trois périodes) au lieu du battement de cœur ; `MemSource` reste
`measured`, et `/proc/hackeros/workers` dit `memory-measure: browser`.

## Constantes

Définies dans `MemoryAccountingConstants` et `AppNetworkGateway`, rappelées
dans [`system-metrics.md`](system-metrics.md), vérifiées par
`MeasuredMemoryAccountantTests.The_constants_are_the_documented_ones` et les
tests de charge :

| Constante | Valeur | Raison |
|---|---|---|
| `ResidentHalfLife` | 10 s | un processus qui vient de construire un gros document reste lourd un moment ; un processus inactif s'efface en une minute (six demi-vies, moins de 2 %) |
| `WindowBytes` | 512 Kio | ce que gestionnaire de fenêtres et compositeur gardent par fenêtre (surface, arbre de rendu, nœuds DOM), invisible au compteur d'allocations puisque le rendu tourne hors du frame de l'app |
| `AppNetworkGateway.SocketBufferBytes` | 144 Kio | tampons TCP par défaut de Linux (`tcp_rmem` 128 Kio, `tcp_wmem` 16 Kio) |

## Utilisation

### Lecture

```text
$ cat /proc/42/status
...
VmRSS:	4712 kB
RssAnon:	3000 kB
RssFile:	1200 kB
VmData:	512 kB
VmExe:	40 kB
HackAllocBytes:	123456789
Cpu:	0.0
CpuSource:	simulated
MemSource:	measured
```

```text
$ cat /proc/meminfo
MemTotal:        4194304 kB
MemFree:         4063232 kB
MemAvailable:    4063232 kB
AnonPages:        102400 kB
Mapped:            20480 kB
Slab:               4096 kB
```

- `MemTotal` reste celui du `VirtualHardwareProfile`.
- Mémoire utilisée = `AnonPages + Mapped + Slab` + mémoire simulée des
  processus restés simulés.
- `HackProcess.WorkingSet64` lit `VmRSS` ; `HackProcess.PrivateMemorySize64`
  lit `RssAnon` (et vaut `WorkingSet64` quand la mémoire est simulée), comme
  `System.Diagnostics.Process`.

### Charger une ressource de l'OS (code de l'OS)

```csharp
using KernelMemoryCharge buffer = ledger.Charge(pid, KernelMemoryKind.Pipe);
buffer.Resize(bufferedChars * sizeof(char));   // à chaque changement
// Dispose libère tout
```

### Composition

`AddHackerOsResourceAccounting` (`HackerOs.Ecosystem.Host`) compose la mesure quand
`plan.MemoryActive` et `ResourceAccountingFeatures.IsMemoryAccountingSupported`
: `AllocationTracker` inscrit sur le diffuseur, `KernelMemoryLedger`
(`IKernelMemoryLedger`), `MeasuredMemoryAccountant` (`IMemoryAccountant`), avec
les images que l'hôte lui passe (`AddHackerOsResourceAccounting(configuration,
images)` ; `AddHackerOsBrowserHost` passe `HostAppCatalog.Images`, le serveur et
`test/test` passent `BuildKnownLazyApps.Images` ; sans images, aucune app n'a de
couche image). Sinon, les implémentations nulles
sont enregistrées. `AddHackerOsEcosystem` passe le registre au presse-papiers,
au shell, aux passerelles de fichiers et de réseau.

## Décisions

- **Image dans `BuiltInApplications.json`, pas dans `manifest.json`.** Le
  manifeste versionné garde exactement le schéma 1 (ADR 0047) ; les tailles
  changent à chaque build et n'ont rien à faire dans un diff de PR. Le
  catalogue de l'hôte est produit dans `obj/` à chaque build : il reçoit un
  champ optionnel `image`.
- **Framework hors image.** Compter `System.*` dans chaque app ferait de
  toutes les apps des géants identiques ; ces assemblies sont l'OS.
- **Lecture paresseuse, pas d'échantillonneur.** La répartition se fait quand
  quelqu'un lit `/proc` ; personne ne lit, aucun travail. Le
  `ResourceSamplingService` n'est pas touché (il nourrit toujours le CPU simulé ; la mesure CPU, ADR 0067, ne s'en sert pas non plus : voir [`cpu-accounting.md`](cpu-accounting.md)).
- **Contrat `IMemoryAccountant` élargi.** `TryGetResidentBytes(pid)` devient
  `TryGetProcessMemory(process, active, …)` et `TryGetSystemMemory(active, …)` :
  le comptable a besoin du type d'app (fenêtre) et de l'id (image), et de la
  table des processus vivants pour répartir le tas, sans dépendre lui-même du
  gestionnaire de processus (singleton contre service par portée).
- **Part noyau retirée du tas avant répartition.** Les tampons des flux et du
  presse-papiers vivent dans le tas : sans ce retrait, ils seraient comptés
  deux fois dans `VmRSS`.
- **Tube chargé au shell.** Sous Linux, les pages d'un tube sont comptées au
  groupe de mémoire de qui le crée ; ici, le shell.

## Tests

| Suite | Couvre |
|---|---|
| `HackerOs.Platform.ResourceAccounting.Tests` | `AllocationTracker` (attribution entre deux changements, première lecture, oubli, vrai crochet), `KernelMemoryLedger`, `MeasuredMemoryAccountant` (constantes, image, part noyau et fenêtre, répartition, seulement après un GC, demi-vie, part noyau retirée, assemblies partagées comptées une fois, simulé ou mesuré, processus morts), commande réelle `ls` sous le vrai crochet |
| `HackerOs.Samples.ServiceApp.Tests` | service réel (`SampleTickerService`) mesuré sous le vrai crochet, sans changement de son code |
| `HackerOs.Ecosystem.Tests` | toutes les apps du catalogue ont leur image ; l'éditeur de texte (fenêtre) mesuré avec la vraie taille de sa DLL ; composition : mesure, registre et observateur présents exactement quand la mémoire est active |
| `HackerOs.Platform.Core.Tests` | format de `status` et `meminfo` mesurés, processus simulé inchangé ; charges du presse-papiers et des tubes ; `HackProcess.PrivateMemorySize64` |
| `HackerOs.AppSdk.Tests` | tampon d'un flux d'écriture chargé puis libéré, rien pour un flux de lecture |
| `HackerOs.Network.Tests` | socket chargée le temps de l'appel |
| `HackerOs.App.Abstractions.Tests`, `HackerOs.Tools.ManifestGenerator.Tests` | champ `image` strict, aller-retour ; agrégation et mesure de l'image ; framework exclu |

## Liste des tâches

- [x] Contrats : `ProcessMemoryUsage`, `SystemMemoryUsage`, `IMemoryAccountant` élargi, `IKernelMemoryLedger`, `KernelMemoryCharge` ; implémentations nulles.
- [x] Couche image : `AppImageInspector`, champ `image` de `BuiltInApplications.json`, cibles MSBuild, `BuildKnownLazyApps.Images`, pour toutes les apps.
- [x] Couche ressources de l'OS : flux d'écriture, presse-papiers, tubes, sockets, coût par fenêtre.
- [x] Couche allocations : `AllocationTracker` sur le crochet de changement de contexte.
- [x] Couche résidente : répartition du tas après GC, décroissance exponentielle, constantes documentées et testées.
- [x] Composition selon l'ADR 0066 ; mesure coupée = comportement exact d'avant.
- [x] `/proc/<pid>/status`, `/proc/meminfo`, `HackProcess.PrivateMemorySize64`.
- [x] Tests sur une app à fenêtre, une commande et un service réels, sans modifier leur code.
- [x] Documentation et index.
