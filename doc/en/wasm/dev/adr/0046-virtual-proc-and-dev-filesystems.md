# ADR 0046 : Systèmes de fichiers virtuels `/proc` et `/dev`

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 1 — Fichiers ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.4, décision D20).
- **Remplace :** aucun ADR. **Complète :** ADR 0008 (ajout du type d'entrée
  `CharacterDevice`, réservé aux fournisseurs virtuels).

## Contexte

Sous Linux, `ps`, `top` et les gestionnaires de tâches lisent `/proc`, et les
scripts redirigent vers `/dev/null`. HackerOS a déjà les données nécessaires :
`IProcessManager` (arbre des processus), `IResourceSimulator` (échantillons CPU,
mémoire, E/S), `VirtualHardwareProfile` et `ISimulationClock`. Il a aussi déjà
le mécanisme de montage : `FileSystemMountRouter` choisit le montage le plus
spécifique et accepte des montages supplémentaires. Mais la composition de l'OS
ne monte aujourd'hui que le fournisseur IndexedDB.

## Décision

### 1. Deux fournisseurs, jamais persistés

| Montage | Classe | `ProviderId` |
|---|---|---|
| `/proc` | `ProcFileSystemProvider` | `procfs` |
| `/dev` | `DevFileSystemProvider` | `devfs` |

- Emplacement : `Platform/HackerOs.Platform.Core/FileSystem/Virtual/`, namespace
  `HackerOs.Platform.Core.FileSystem.Virtual`. Les deux implémentent
  `IFileSystemProvider`.
- **Calculés à chaque lecture.** Aucun appel au stockage, rien n'est écrit dans
  IndexedDB (D20).
- Branchement dans `EcosystemServiceCollectionExtensions`, utilisé par les deux
  hôtes WASM :
  `new FileSystemMountRouter(indexedDbProvider, [new FileSystemMount(VirtualPath.Parse("/proc"), procfs), new FileSystemMount(VirtualPath.Parse("/dev"), devfs)])`.
  Les montages sont protégés (`isProtectedRoot: true`).
- **Points de montage visibles :** le routeur ne fusionne pas les montages dans
  le listage du parent. `FileSystemSeeder` crée donc les dossiers vides `/proc`
  (`system:system 0555`) et `/dev` (`system:system 0755`) dans le fournisseur
  racine, comme de vrais points de montage. `ls /` les montre, et le routeur les
  masque ensuite par le fournisseur virtuel.
- **Identité des entrées :** `FileSystemEntryId` stable, tiré des 16 premiers
  octets du SHA-256 de `"<providerId>:<chemin>"`. Pour `/proc/<pid>`, le PID
  n'est jamais réutilisé pendant un démarrage (ADR 0012), donc l'identité reste
  stable.
- **Révision :** `ISimulationClock.CurrentTick` au moment de la lecture. Les
  horodatages sont l'heure de démarrage du processus pour `/proc/<pid>`, l'heure
  de démarrage de l'OS sinon, et l'heure courante pour le contenu des fichiers.
- **Taille :** `0` pour tous les fichiers virtuels, comme sous Linux. Le contenu
  est produit en flux : un lecteur lit jusqu'à la fin de flux, sans se fier à la
  taille.
- **Surveillance :** aucun événement de `FileSystemChangeEvent` n'est émis pour
  ces montages. `inotify` ne fonctionne pas non plus sur `/proc` sous Linux.

### 2. Opérations non permises

Les entrées sont exposées avec des bits de mode en lecture seule. Toute
création, suppression, déplacement, copie vers le montage ou changement de
permissions répond `PermissionDenied`, comme le refus `EACCES` de Linux. Une
écriture dans un fichier de `/proc` répond aussi `PermissionDenied`. Le
`CopyAsync` **depuis** `/proc` vers un autre montage est géré par la façade de
l'ADR 0043, qui copie le flux.

L'autorisation suit le chemin ordinaire du `FileSystemService` : capacité selon
le chemin (`/proc` et `/dev` étant hors du dossier personnel, il faut
`filesystem.system.read`, ou `filesystem.system.write` pour écrire dans `/dev`),
autorité, puis bits de mode du fournisseur.

### 3. Contenu de `/proc`

**Visibilité des processus :** `/proc` ne liste que les processus actifs (pas
`Stopped`). L'appelant voit toujours son propre processus. Il voit les autres
s'il a la capacité `process.list` ou l'autorité `System`, comme
`IAppProcessGateway.ListProcesses`. Un PID invisible répond `NotFound`.

| Chemin | Type et mode | Contenu |
|---|---|---|
| `/proc/<pid>/` | dossier `0555`, propriétaire = utilisateur du processus | — |
| `/proc/<pid>/status` | fichier `0444` | voir ci-dessous |
| `/proc/<pid>/cmdline` | fichier `0444` | `appId`, puis les arguments, séparés et terminés par `\0`. **Phase 1 : `appId` seul**, puisque `ProcessRecord` n'a pas encore les arguments (phase 3). |
| `/proc/<pid>/environ` | fichier `0400` | `CLÉ=valeur\0…`. **Phase 1 : vide.** Rempli par l'environnement par processus de la phase 3 (D13). |
| `/proc/<pid>/cwd` | lien symbolique `0777` | répertoire courant. **Phase 1 : `/home/<user>`.** Phase 3 : répertoire courant du processus. |
| `/proc/self` | lien symbolique `0777` | `/proc/<pid de l'appelant>`. Absent si l'appelant n'a pas de processus. |
| `/proc/meminfo` | fichier `0444` | `MemTotal`, `MemFree`, `MemAvailable` en kB |
| `/proc/cpuinfo` | fichier `0444` | un bloc par cœur virtuel |
| `/proc/uptime` | fichier `0444` | `<secondes depuis le démarrage> <secondes d'inactivité>` |
| `/proc/loadavg` | fichier `0444` | `<1 min> <5 min> <15 min> <actifs>/<total> <dernier pid>` |

Format de `/proc/<pid>/status`, des paires `Clé:\tvaleur` dans cet ordre :

```text
Name:	<nom court de l'app, tronqué à 15 caractères comme sous Linux>
State:	<lettre> (<mot>)
Pid:	<pid>
PPid:	<pid parent, ou 0>
Uid:	<UserId>
AppId:	<appId>
Kind:	<window|terminal|service>
Session:	<SessionId>
VmRSS:	<mémoire du dernier échantillon, en kB> kB
Cpu:	<CpuUsage du dernier échantillon, en %>
```

Correspondance `ProcessState` → `State` : `Created` → `I (idle)`, `Starting` →
`S (sleeping)`, `Running` → `R (running)`, `Stopping` → `T (stopping)`, `Faulted`
→ `Z (zombie)`.

Sources des chiffres :

- mémoire et CPU par processus : dernier `ProcessResourceSample` de
  `IResourceSimulator.GetHistory(pid)` ;
- `MemTotal` : `VirtualHardwareProfile.MemoryCapacityBytes` ;
- `MemFree` : `MemTotal` moins le `TotalMemoryUsageBytes` du dernier
  `SystemResourceSample` ;
- nombre de cœurs de `cpuinfo` : `ceil(VirtualHardwareProfile.CpuCapacity)` ;
- `uptime` : `CurrentTick × TickDuration` d'`ISimulationClock` ;
- `loadavg` : moyennes glissantes de `TotalCpuUsage` sur l'historique système
  borné.

Pour connaître l'appelant (`/proc/self`), on ajoute à `AppOperationContext`
(`Shared/HackerOs.App.Abstractions/AppOperationContext.cs`) une propriété
`long? ProcessId { get; init; }`. C'est la valeur brute, parce que
`App.Abstractions` ne référence pas le type `ProcessId`.
`AppExecutionContextFactory` la renseigne.

### 4. Contenu de `/dev`

Nouveau membre `FileSystemEntryKind.CharacterDevice` (complète ADR 0008).
`ls -l` l'affiche avec le type `c`. Le fournisseur IndexedDB refuse de
persister ce type (`UnsupportedOperation`). Les `switch` sur `FileSystemEntryKind`
dans la plateforme et les apps de base doivent être revus pour ce nouveau cas.

| Chemin | Mode | Lecture | Écriture |
|---|---|---|---|
| `/dev/null` | `0666` | fin de flux immédiate | acceptée et ignorée |
| `/dev/zero` | `0666` | flux infini d'octets `0x00` | acceptée et ignorée |
| `/dev/random` | `0666` | flux infini d'octets aléatoires | acceptée et ignorée |
| `/dev/urandom` | `0666` | identique à `/dev/random`, comme sous Linux moderne | acceptée et ignorée |

- L'aléa vient de `System.Security.Cryptography.RandomNumberGenerator`, et non
  d'`ISimulationRandom` : l'ADR 0012 réserve celui-ci à la simulation
  déterministe et l'interdit pour tout usage cryptographique.
- **Flux infinis :** ils lisent par blocs de 4 Kio et respectent l'annulation.
  `HackFile.ReadAll*` (ADR 0043) lève `HackIOException` sur une entrée
  `CharacterDevice`, au lieu de geler l'unique thread du navigateur. Il faut
  passer par `HackFile.OpenReadAsync`, comme `head -c 16 /dev/urandom` le ferait.
- Une écriture dans un périphérique réussit sans changer de révision ni émettre
  d'événement.

### 5. Consommateurs

- `ps` et le gestionnaire de tâches liront `/proc`. La migration de `ps` se fait
  en phase 3 avec les commandes, et celle du gestionnaire de tâches en phase 4
  (plan, section 8.2). Ils gardent `IAppProcessGateway` pour les actions (kill).
- La redirection `> /dev/null` arrive avec la grammaire du shell de la phase 3.

### 6. Tests de contrat

Dans `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/` :

- listage de `/` contenant `proc` et `dev` ;
- format exact de `status`, `meminfo`, `uptime` et `loadavg` avec une horloge et
  un simulateur déterministes ;
- visibilité des PID selon `process.list` ;
- résolution de `/proc/self` ;
- refus des mutations (`PermissionDenied`) ;
- lecture vide de `/dev/null` et écriture ignorée ;
- lecture bornée de `/dev/zero` par flux ;
- absence de toute écriture dans le dépôt IndexedDB simulé.

## Conséquences

- `cat /proc/meminfo`, `ls /proc` et `echo x > /dev/null` (phase 3) se
  comportent comme sous Linux.
- Le contenu de `/proc` est toujours à jour, sans coût de stockage. En
  contrepartie, chaque lecture recalcule le contenu ; un gestionnaire de tâches
  qui interroge souvent doit garder un rythme raisonnable (1 s).
- Le nouveau type `CharacterDevice` touche tous les `switch` exhaustifs sur
  `FileSystemEntryKind` (explorateur, `ls`, `FileView`).

## Références

- ADR 0008 : modèle du système de fichiers virtuel (complété).
- ADR 0012 : processus, horloge, ressources et aléa.
- ADR 0043 : API `Hack*`.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 5.4, 7.2 et 8.2.
- `Platform/HackerOs.Platform.Core/FileSystem/FileSystemMountRouter.cs`, `Platform/HackerOs.Platform.Core/Processes/`.
