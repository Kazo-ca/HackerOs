# Plan de refonte de l'écosystème HackerOS

**Statut :** plan validé avec Benoit le 2026-10-10. Les quatre phases sont
réalisées et fusionnées dans `rebuild/base` (phase 4 terminée le 2026-10-11 par
`rebuild/4-sdk-boundary-final`) ; la section 12 renvoie au document de chaque
chantier. Ce document remplace
`old/implementation-status.md` et `old/integration-task-list.md` comme
référence de direction pour l'écosystème. Ces deux documents restent dans
[`old/`](../old/) comme historique.

## 1. Objet

Remettre l'écosystème de `wasm2/HackerOs` sur de bonnes bases. Le système
actuel fonctionne, mais il est plus compliqué que le legacy TypeScript de `src/`
sans être plus agréable à utiliser ni plus simple à étendre. Le but est double,
et les deux volets comptent autant l'un que l'autre :

1. **Pour le développeur d'app :** ajouter une application doit être simple.
   L'API fichier ressemble à `System.IO`, le manifeste est une classe C#, et
   une seule `ProjectReference` suffit à installer une app.
2. **Pour l'utilisateur :** l'OS se comporte comme un vrai système Linux.
   Tout est fichier (paramètres, journaux, processus), le shell est complet,
   et les apps de base sont au moins aussi riches que dans le legacy.

Une hypothèse guide tout le plan : un système moins compliqué laisse moins de
place aux bugs d'intégration.

Le périmètre est l'écosystème : système de fichiers, modèle d'app, services de
l'OS et apps de base (explorateur de fichiers, console, gestionnaire de
tâches, éditeur de texte). Les autres apps, le serveur et le mobile sont hors périmètre.
**Aucun jeu sur ce repository** ([ADR 0135](../adr/0135-no-game-in-this-repository.md)).

## 2. Constat de départ

### 2.1 Ce qui est solide et qu'on garde

- Le moteur de fichiers (`HackerOs.Simulation.Abstractions.FileSystem`,
  `Platform.Core/FileSystem`) : chemins canoniques, identités d'entrées,
  révisions optimistes, transactions atomiques, liens symboliques,
  permissions owner/group/other, montages par chemin.
- La persistance IndexedDB par blocs dédupliqués
  (`Infrastructure/HackerOs.Infrastructure.Browser`).
- Le fenêtrage et la barre des tâches (`HackerOs.Windowing.*`,
  `HackerOs.Taskbar.Blazor`), le mobile, le thème.
- Le gestionnaire de processus (arbre parent/enfants, kill en cascade), les
  événements, la messagerie par sujets, les notifications, le presse-papiers,
  la surveillance de fichiers, les sessions et `sudo`.

### 2.2 Ce qui ne va pas

| Sujet | Problème constaté | Référence |
|---|---|---|
| API fichier | Écrire une chaîne exige `CreateAsync` avec des bits de mode, puis `WriteAsync` avec la révision attendue, puis une classe `IFileSystemContentSource` maison. | `Apps/System/HackerOs.Apps.TextEditor/TextEditorWindow.razor` (≈ l. 364-460) |
| Manifeste | Défini deux fois : `app.manifest.json` et une copie C# `StaticManifest` (32 apps). | `Apps/**/app.manifest.json` |
| Ajout d'une app | Un projet, plus trois entrées dans `HackerOs.Ecosystem.csproj` (`ProjectReference`, `BlazorWebAssemblyLazyLoad`, `EmbeddedResource`). | `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`, `BuildKnownLazyApps.cs` |
| Paramètres | Le montage `/etc/hackeros` n'est jamais branché dans l'OS réel : le routeur est créé avec le seul fournisseur IndexedDB. `SettingsFileSystemProvider` n'existe que dans les tests. Les paramètres vivent dans un magasin à part, et la sécurité vient du service de paramètres, pas des droits du fichier. | `OS/HackerOs.Ecosystem/EcosystemServiceCollectionExtensions.cs` (≈ l. 157) |
| Journaux | `/var/log` est créé mais reste vide. Les logs vont dans un magasin IndexedDB lu uniquement par Error Log Viewer. | `Platform.Core/Diagnostics/` |
| Shell | Pas de pipes, de redirections, de `&&` ni de variables d'environnement. Le legacy avait les pipes et `env`. | `Shared/HackerOs.AppSdk.Terminal/ShellParser.cs`, `adr/0014-shell-grammar-boundary.md` |
| Commandes | 28 projets, un par commande. `cd`, `pwd`, `clear` et `help` sont codées en dur dans le terminal. | `Apps/Commands/`, commentaire dans `HackerOs.Ecosystem.csproj` |
| Corbeille | Inexistante : toute suppression est définitive. | — |
| Permissions | Toutes les capacités déclarées sont accordées automatiquement à la connexion, sans rien demander. | `Platform.Core/Policy/CleanProfileCapabilityGrantSeeder.cs` |
| Apps système | Elles injectent des services internes (`ISessionService`, `AppCatalog`, `AppLifecycleOrchestrator`, `IJSRuntime`…). Un développeur externe ne peut pas écrire d'équivalent. | `Apps/System/*/*.razor` (`@inject`) |
| Apps de base | Explorateur sans couper/copier/coller ni corbeille. Gestionnaire de tâches limité aux processus et services. Éditeur sans remplacer. | voir section 7 |

## 3. Principes directeurs

1. **Refonte sur place.** On travaille dans `wasm2/HackerOs`. On garde le
   moteur de fichiers, IndexedDB et le fenêtrage, et on remplace les couches du
   dessus. On ne crée pas de nouveau dossier racine.
2. **Tout est fichier.** Paramètres, journaux, processus et périphériques
   sont lisibles, et modifiables quand c'est permis, par le système de
   fichiers. Les droits du fichier font foi.
3. **Un seul SDK public.** Les apps système utilisent exactement le même SDK
   que les apps tierces. Si une app système a besoin de quelque chose, on
   l'ajoute au SDK, protégé par une capacité.
4. **Simplicité pour le développeur.** La rigueur du moteur (révisions,
   transactions, flux) reste à l'intérieur. Le développeur voit une API
   familière.
5. **Imiter Linux.** Quand un comportement existe sous Linux, on le reproduit,
   sauf raison explicite.
6. **Nouveaux ADR plutôt que modifications.** Chaque décision qui contredit un
   ADR accepté fait l'objet d'un nouvel ADR qui le remplace (section 9).

## 4. Registre des décisions

Décisions prises avec Benoit pendant la discussion du 2026-10-10.

| # | Sujet | Décision |
|---|---|---|
| D1 | Objectif | Simplicité pour le développeur et qualité pour l'utilisateur, à égalité. |
| D2 | Moteur de fichiers | On le garde et on ajoute une façade simple dans le SDK. |
| D3 | API fichier | API de type `System.IO`, avec le préfixe `Hack` sur chaque type : `HackFile`, `HackDirectory`, `HackPath`, `HackFileInfo`, `HackDirectoryInfo`, `HackFileStream`… |
| D4 | Forme de l'API | Statique, comme `System.IO`. L'app appelante est identifiée par un contexte d'exécution ambiant, invisible pour le développeur. Méthodes asynchrones seulement (IndexedDB est asynchrone et le navigateur n'a qu'un thread). |
| D5 | Erreurs fichier | Des exceptions, comme `System.IO` : `HackFileNotFoundException`, `HackDirectoryNotFoundException`, `HackUnauthorizedAccessException`, `HackIOException`… |
| D6 | Manifeste | Une classe C# marquée `[AppManifest]`, séparée de la fenêtre, qui hérite d'une base abstraite. Propriétés essentielles `abstract` (obligatoires), les autres `virtual` avec une valeur par défaut. |
| D7 | Découverte | À la compilation. Un générateur produit le `manifest.json` dans le dossier de l'app. L'hôte agrège les apps référencées dans `BuiltInApplications.json`, que l'OS lit au démarrage. Le JSON n'est jamais écrit à la main. Il servira aussi au futur magasin d'apps. |
| D8 | Installation | Une `ProjectReference` dans l'hôte suffit. Aucune autre ligne à ajouter. |
| D9 | Apps système | Définies par une liste de confiance dans l'hôte. Une app ne peut pas se déclarer système elle-même. |
| D10 | Désinstallation | L'app est cachée partout, son lancement est bloqué et ses associations de fichiers sont retirées. Elle reste réinstallable depuis la liste. |
| D11 | Portée de la désinstallation | Un administrateur désinstalle pour toute la machine. Un utilisateur désinstalle pour son seul profil. Les apps système ne sont pas désinstallables. |
| D12 | Shell | Pipes `\|`, redirections `>` et `>>`, `&&`, variables d'environnement. |
| D13 | Processus | Chaque processus a son répertoire courant et son environnement, hérités du parent. |
| D14 | Commandes | Toutes les commandes deviennent de vraies commandes, sans exception codée en dur dans le terminal. Elles sont regroupées dans un seul projet. |
| D15 | Corbeille | Une corbeille partagée entre l'explorateur et `rm`. |
| D16 | Paramètres | Ce sont de vrais fichiers, seule source de vérité. Les droits du fichier décident de l'accès : si on peut modifier le fichier, on peut modifier le paramètre. |
| D17 | Fichier invalide | L'écriture est acceptée. L'app ignore les clés invalides, utilise ses valeurs par défaut et écrit un avertissement dans le journal. |
| D18 | Niveaux de paramètres | En couches : `/etc/<app>/` (système), puis `~/.config/<app>/` (utilisateur). L'utilisateur hérite des valeurs système et peut les surcharger, sauf les clés verrouillées par un administrateur. |
| D19 | Journaux | De vrais fichiers texte dans `/var/log`, avec taille limitée et rotation. |
| D20 | `/proc` et `/dev` | Purement virtuels : calculés à la lecture, jamais écrits dans IndexedDB. |
| D21 | Apps de base | Uniquement le SDK public. |
| D22 | Fonctionnalités des apps de base | Au minimum la parité avec le legacy, plus les ajouts de la section 7. |
| D23 | Permissions dangereuses | Les capacités sont classées en normales et dangereuses. Les apps système les reçoivent d'office. Toute autre app déclenche une fenêtre d'approbation spéciale. |
| D24 | Approbation | Seul un administrateur peut approuver, avec son mot de passe, comme `sudo`. |
| D25 | Politique par défaut | « Autoriser avec approbation ». Un administrateur peut passer à « Toujours bloquer ». |
| D26 | Ordre | 1. Fichiers ; 2. Modèle d'app ; 3. Shell, commandes et corbeille ; 4. SDK complet et apps de base. |
| D27 | Plus tard | Isolation d'exécution des apps (watchdog, Web Workers). |
| D28 | Documentation | Plan en français, noms de fichiers en anglais. Les anciens documents sont déplacés dans `docs/old/`, sauf `docs/adr/`. |

## 5. Phase 1 — Fichiers

### 5.1 API `Hack*` de type System.IO

Nouveau namespace public dans le SDK (proposé : `HackerOs.AppSdk.IO`), sans
dépendance à Blazor, utilisable par les apps fenêtre, terminal et service.

**Types prévus**, calqués sur `System.IO` :

| Type | Équivalent .NET | Exemples de membres |
|---|---|---|
| `HackFile` | `File` | `ReadAllTextAsync`, `WriteAllTextAsync`, `AppendAllTextAsync`, `ReadAllBytesAsync`, `WriteAllBytesAsync`, `ReadAllLinesAsync`, `ExistsAsync`, `CopyAsync`, `MoveAsync`, `DeleteAsync`, `OpenReadAsync`, `OpenWriteAsync` |
| `HackDirectory` | `Directory` | `CreateDirectoryAsync`, `ExistsAsync`, `GetFilesAsync`, `GetDirectoriesAsync`, `EnumerateFileSystemEntriesAsync`, `MoveAsync`, `DeleteAsync(recursive)` |
| `HackPath` | `Path` | `Combine`, `GetFileName`, `GetExtension`, `GetDirectoryName`, `GetFullPath` (avec `~` et répertoire courant) |
| `HackFileInfo` / `HackDirectoryInfo` | `FileInfo` / `DirectoryInfo` | `Length`, `LastWriteTimeUtc`, `Exists`, `Permissions`, `Owner`, `RefreshAsync` |
| `HackFileStream` | `FileStream` | Flux lecture/écriture qui publie le contenu au `DisposeAsync` |
| `HackFileSystemWatcher` | `FileSystemWatcher` | Événements créé, modifié, supprimé, renommé (au-dessus de la surveillance existante) |

**Fonctionnement interne :**
- Chaque appel passe par la passerelle existante `IAppFileSystemGateway`,
  donc les capacités et les droits restent vérifiés exactement comme
  aujourd'hui.
- L'app appelante est identifiée par un contexte ambiant (de type
  `AsyncLocal`) que l'OS positionne avant d'exécuter du code d'app. Le
  développeur n'a rien à passer.
- `WriteAllTextAsync` crée le fichier s'il n'existe pas, avec des droits par
  défaut dérivés d'un umask (`0644` pour un fichier, `0755` pour un dossier),
  puis écrit. La révision est gérée à l'intérieur : la façade lit la révision
  courante et réessaie une fois si elle a changé. Une API avancée reste
  disponible pour qui veut un contrôle strict des conflits (l'éditeur de
  texte, par exemple).
- Les codes `FileSystemErrorCode` sont traduits en exceptions `Hack*`, dans
  l'esprit des exceptions de `System.IO`.
- Pas de méthodes synchrones : impossibles sans bloquer l'unique thread du
  navigateur.

### 5.2 Paramètres en fichiers

Mise en œuvre : [`settings-files.md`](../../framework/settings-files.md).

- **Source de vérité :** le fichier. Le magasin IndexedDB `settings` séparé
  disparaît pour les paramètres d'app et de système. Une migration copie les
  valeurs existantes dans les fichiers au premier démarrage.
- **Emplacements :**
  - système : `/etc/<app>/<document>.config` (et `/etc/hackeros/` pour l'OS) ;
  - utilisateur : `~/.config/<app>/<document>.config`.
- **Couches :** valeur par défaut déclarée par l'app, puis `/etc`, puis
  `~/.config`. Une clé que l'administrateur verrouille dans `/etc` ne peut pas
  être surchargée. La syntaxe du verrou reste à définir dans l'ADR (section 10).
- **Sécurité :** uniquement les droits du fichier. `/etc/<app>/` appartient à
  `root` (`0644`), `~/.config/<app>/` à l'utilisateur.
- **Format :** le format `.config` ligne par ligne de l'ADR 0011 (`clé=valeur`,
  `[Groupe]`, `#` commentaire) est conservé.
- **Erreurs :** une écriture invalide est acceptée. À la lecture, l'app ignore
  les clés invalides, garde ses valeurs par défaut et écrit un avertissement
  dans `/var/log/apps/<app>.log`.
- **API typée :** l'app lit et écrit des valeurs typées par une petite API du
  SDK (nom proposé : `HackSettings`) qui fait la fusion des couches et
  surveille les fichiers pour signaler les changements. L'app de Paramètres et
  `nano /etc/...` voient et modifient les mêmes fichiers.

### 5.3 Journaux

- Fichiers texte :
  - `/var/log/syslog` : système ;
  - `/var/log/auth.log` : connexions, `sudo`, approbations de permissions ;
  - `/var/log/apps/<app>.log` : un fichier par app.
- Format de ligne inspiré de syslog : date ISO, niveau, source, message.
- Taille limitée par fichier, avec rotation `.1`, `.2`… et un nombre maximal
  d'archives (valeurs dans l'ADR).
- `ILogger` et `IAppLoggingGateway` écrivent dans ces fichiers. Error Log
  Viewer, `cat`, `tail` et `grep` les lisent.
- Écriture groupée en arrière-plan pour ne pas ralentir l'app qui journalise.

### 5.4 `/proc` et `/dev` virtuels

- Deux nouveaux fournisseurs montés dans le routeur existant
  (`FileSystemMountRouter`), en lecture seule, calculés à chaque lecture et
  jamais persistés.
- `/proc/<pid>/` : `status` (nom, état, utilisateur, parent), `cmdline`,
  `environ`, `cwd` (lien), plus `/proc/meminfo`, `/proc/cpuinfo`, `/proc/uptime`
  et `/proc/loadavg`, alimentés par le simulateur de ressources existant.
- `/dev/null` (écriture absorbée, lecture vide), `/dev/zero`, `/dev/random` et
  `/dev/urandom`.
- `ps` et le gestionnaire de tâches lisent `/proc`.

## 6. Phase 2 — Modèle d'app

### 6.1 Manifeste en classe C#

```csharp
[AppManifest]
public sealed class TextEditorApp : AppDefinition
{
    public override string Id => "org.hackeros.text-editor";
    public override string Name => "Text Editor";
    public override AppKind Kind => AppKind.Window;
    public override Type EntryPoint => typeof(TextEditorWindow);

    public override string Version => "1.0.0";
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md", ".log"], ["open", "edit"])];
}
```

- `AppDefinition` (nom proposé) remplace `StaticManifest` et
  `app.manifest.json` comme source unique.
- Propriétés `abstract` : identifiant, nom, type d'app, point d'entrée.
- Propriétés `virtual` avec valeur par défaut : version, description, éditeur,
  catégorie, icône, capacités, associations de fichiers, métadonnées de
  commande, points d'entrée par plateforme (bureau, mobile), instance unique,
  profil de ressources.
- La validation actuelle (`AppManifestValidator`) s'exécute à la compilation,
  dans le générateur, et l'erreur fait échouer le build.

### 6.2 Génération et découverte

1. **Dans chaque projet d'app :** une tâche MSBuild exécutée après la
   compilation instancie la classe `[AppManifest]` et écrit `manifest.json`
   dans le dossier de l'app. Un générateur Roslyn ne peut pas écrire dans le
   dossier source, d'où la tâche MSBuild. Le fichier est généré : on ne
   l'édite jamais à la main.
2. **Dans l'hôte :** une tâche MSBuild rassemble les `manifest.json` des
   projets référencés dans `BuiltInApplications.json`, embarqué dans l'hôte.
3. **Au démarrage :** l'OS lit `BuiltInApplications.json`. Il connaît donc
   toutes les apps sans charger leurs DLL, et le chargement différé est
   conservé.
4. **Une seule référence :** un fichier `.targets` partagé ajoute
   automatiquement `BlazorWebAssemblyLazyLoad` et la ressource embarquée pour
   chaque app référencée. Les listes manuelles de `HackerOs.Ecosystem.csproj`
   et `BuildKnownLazyApps.cs` disparaissent.

### 6.3 Apps système et désinstallation

- **Liste de confiance :** l'hôte déclare ses apps système (par exemple dans
  un fichier de configuration de l'hôte ou une propriété MSBuild sur la
  `ProjectReference`). Une app ne peut pas s'y inscrire elle-même.
- **Désinstaller** = cacher du lanceur et de la recherche, bloquer le
  lancement, retirer les associations de fichiers. Les données de l'app ne
  sont pas supprimées.
- **Réinstaller** depuis Paramètres > Applications installées.
- **Portée :**
  - administrateur : pour toute la machine (état dans `/etc/hackeros/apps.config`) ;
  - utilisateur : pour son profil (état dans `~/.config/hackeros/apps.config`).
- Les apps système ne proposent pas de bouton de désinstallation.
- Le mécanisme d'activation existant (ADR 0032) est réutilisé et étendu.

### 6.4 Permissions dangereuses

- **Classement :** chaque capacité du catalogue `AppCapabilities` est marquée
  normale ou dangereuse. Dangereuses, au minimum : lecture ou écriture
  hors du dossier personnel, écriture dans `/etc`, kill des processus
  d'autres apps, gestion des apps (installer, désinstaller, activer),
  paramètres système, accès réseau brut. La liste exacte est fixée par l'ADR.
- **Apps système :** elles reçoivent leurs capacités dangereuses d'office.
- **Autres apps :** les capacités normales restent accordées automatiquement,
  comme aujourd'hui. Une capacité dangereuse n'est jamais accordée sans
  approbation : `CleanProfileCapabilityGrantSeeder` est modifié en ce sens.
- **Fenêtre d'approbation spéciale :** visuellement distincte des dialogues
  ordinaires (bandeau d'avertissement, liste des permissions demandées et de
  leurs risques). Elle exige le mot de passe d'un administrateur, comme
  `sudo`. Chaque décision est écrite dans `/var/log/auth.log`.
- **Politique :** réglage administrateur dans `/etc/hackeros/security.config`,
  avec deux valeurs : « autoriser avec approbation » (par défaut) et « toujours
  bloquer ».

## 7. Phase 3 — Shell, commandes et corbeille

### 7.1 Grammaire du shell

- Pipes `cmd1 | cmd2 | cmd3` : la sortie standard d'une commande devient
  l'entrée standard de la suivante.
- Redirections `>` (remplacer) et `>>` (ajouter) vers un fichier.
- `&&` : exécuter la suite seulement si la commande précédente a réussi (code
  de sortie 0).
- Variables d'environnement : expansion de `$VAR` et `${VAR}`, affectation par
  `export VAR=valeur`, affichage par `env`. Variables de base : `HOME`, `USER`,
  `PWD`, `PATH`, `SHELL`.
- Remplace l'ADR 0014, qui excluait ces éléments de la première tranche.

### 7.2 Environnement par processus

- Chaque processus a un répertoire courant et un dictionnaire d'environnement,
  copiés depuis le parent à la création.
- Visible dans `/proc/<pid>/environ` et `/proc/<pid>/cwd`.
- Accessible dans le SDK (nom proposé : `HackEnvironment`, calqué sur
  `System.Environment`).

### 7.3 Commandes

- Les 28 projets `Apps/Commands/HackerOs.Commands.*` sont fusionnés dans un seul
  projet (nom proposé : `HackerOs.Commands`). Chaque commande garde sa classe
  et sa déclaration `[AppManifest]`.
- `cd`, `pwd`, `clear` et `help` deviennent des commandes normales du
  catalogue. Comme sous Linux, `cd` et `export` agissent sur le shell
  lui-même : le SDK leur donne un accès contrôlé à la session du shell
  appelant.
- Le format des commandes tierces reste le même : une app de type Terminal
  avec sa classe manifeste.

### 7.4 Corbeille

- Emplacement standard Linux (spécification freedesktop) :
  `~/.local/share/Trash/files/` pour les fichiers, `~/.local/share/Trash/info/`
  pour l'origine et la date de suppression.
- L'explorateur et `rm` envoient à la corbeille. L'explorateur propose
  restaurer, supprimer définitivement et vider la corbeille.
- API SDK pour les apps (nom proposé : `HackTrash`).

## 8. Phase 4 — SDK complet et apps de base

### 8.1 Le SDK suffit aux apps système

- Retirer des apps système toute injection de service interne
  (`ISessionService`, `AppCatalog`, `AppLifecycleOrchestrator`,
  `IPersistentAppCatalogRepository`, services de synchronisation,
  `IJSRuntime` direct…).
- Pour chaque besoin, ajouter au SDK une API publique protégée par une
  capacité, normale ou dangereuse selon la section 6.4. Exemples : identité de
  l'utilisateur courant, lecture du catalogue d'apps, démarrage et arrêt
  d'apps ou de services, téléchargement et envoi de fichiers navigateur.
- Un test d'architecture fait échouer le build si un projet sous `Apps/`
  référence `HackerOs.Platform.Core` ou `HackerOs.Platform.Blazor`.

### 8.2 Fonctionnalités cibles

Au minimum la parité avec le legacy (`src/apps/`), plus ces ajouts.

**Explorateur de fichiers**
- Existant à conserver : navigation, historique, création, renommage,
  suppression, zip, propriétés, « ouvrir avec », envoi et téléchargement.
- À ajouter : couper, copier, coller (avec le presse-papiers du SDK),
  corbeille (section 7.4), glisser-déposer.

**Gestionnaire de tâches**
- Existant à conserver : liste des processus et services, kill, mode de
  démarrage des services.
- À ajouter : onglet performance avec graphiques CPU et mémoire, onglets
  disque et réseau comme dans le legacy, détail par processus, filtre,
  sélection multiple. Toutes les données viennent de `/proc`.

**Éditeur de texte**
- Existant à conserver : ouvrir, enregistrer, enregistrer sous, recherche,
  détection des conflits d'écriture.
- À ajouter : remplacer et tout remplacer.

**Console**
- Existant à conserver : historique et répertoire courant.
- À ajouter : complétion par Tab (commandes et chemins), plusieurs onglets.
  Pipes, redirections, `&&` et variables viennent de la phase 3.

## 9. Hors périmètre pour l'instant

- Isolation d'exécution des apps (watchdog, Web Workers) : une app avec une
  boucle infinie gèle encore tout l'OS. **Reprise par la phase 5**
  ([`execution-isolation-plan.md`](execution-isolation-plan.md), ADR 0066 à
  0070, 2026-10-16).
- Magasin d'apps et installation d'apps tierces au runtime. Le format
  `manifest.json` généré est déjà pensé pour lui.
- Serveur, synchronisation, mobile et réseau simulé : non
  modifiés par ce plan, sauf adaptation aux nouvelles API.

## 10. ADR à écrire

Chaque ADR ci-dessous remplace explicitement l'ADR existant cité, qui n'est
pas modifié.

| Nouvel ADR | Remplace | Contenu |
|---|---|---|
| [0043](../adr/0043-hack-file-api-and-ambient-context.md) API fichier `Hack*` et contexte ambiant | — | D3, D4, D5, section 5.1 |
| [0044](../adr/0044-settings-as-files.md) Paramètres en fichiers | 0004, 0011 (partiellement), 0029 | D16 à D18, section 5.2, syntaxe du verrou |
| [0045](../adr/0045-logs-as-files.md) Journaux en fichiers | — | D19, section 5.3, tailles de rotation |
| [0046](../adr/0046-virtual-proc-and-dev-filesystems.md) Systèmes de fichiers virtuels `/proc` et `/dev` | complète 0008 | D20, section 5.4 |
| [0047](../adr/0047-app-definition-and-generated-manifests.md) Manifeste en classe C# et génération | 0010 (en partie) | D6, D7, D8, section 6.1-6.2 |
| [0048](../adr/0048-system-apps-and-uninstall.md) Apps système et désinstallation | complète 0032, remplace en partie 0033 | D9 à D11, section 6.3 |
| [0049](../adr/0049-dangerous-capabilities-and-admin-approval.md) Permissions dangereuses et approbation | complète 0002, 0003, 0042 | D23 à D25, section 6.4 |
| [0050](../adr/0050-shell-grammar-and-process-environment.md) Grammaire du shell et environnement de processus | 0014 | D12, D13, section 7.1-7.2 |
| [0051](../adr/0051-unified-commands-project.md) Commandes regroupées, sans exception codée en dur | 0034 (section 1) | D14, section 7.3 |
| [0052](../adr/0052-trash.md) Corbeille | — | D15, section 7.4 |
| [0055](../adr/0055-base-apps-on-public-sdk-only.md) Apps de base sur le SDK public uniquement | complète 0019 | D21, section 8.1 : références et injections permises, vérification au build, agent d'authentification |
| [0056](../adr/0056-sdk-apis-for-system-apps.md) API du SDK qui remplacent les services internes | complète 0044, 0048, 0049 | section 8.1 : une API et une capacité par besoin des apps système |
| [0057](../adr/0057-proc-metrics-for-task-manager.md) Mesures système dans `/proc` et `HackDriveInfo` | complète 0046 | section 8.2, gestionnaire de tâches |
| [0058](../adr/0058-base-app-features.md) Fonctionnalités des apps de base | — | D22, section 8.2, chantiers `rebuild/4-*` |
| [0059](../adr/0059-sdk-boundary-diagnostic-codes.md) Codes des diagnostics de la frontière du SDK | 0055 (numéros des codes) | `HOSAPP0030` et `HOSAPP0031`, les numéros de l'ADR 0055 étant déjà pris |
| [0060](../adr/0060-system-administration-sdk-adjustments.md) Ajustements de l'API d'administration du système | 0056 (sections 8, 9 et 11, en partie) | section 8.1 : `SystemPower`, `HackPermissionDecision`, écriture des documents de l'OS, presse-papiers de l'hôte pour Diagnostic |

## 11. Points de détail à fixer dans les ADR

Ces points n'influencent pas la direction du plan. Ils sont tranchés dans
l'ADR correspondant, avec la valeur proposée ci-dessous par défaut.

- `rm` et la corbeille : `rm` envoie à la corbeille et `rm --permanent`
  supprime définitivement. **Proposé.**
- Syntaxe du verrou administrateur dans `/etc` : une section `[locked]` qui
  liste les clés non surchargeables. **Proposé.**
- Taille de rotation des journaux : 1 Mio par fichier, 5 archives. **Proposé.**
- Emplacement de la liste des apps système : propriété MSBuild
  `IsSystemApp="true"` sur la `ProjectReference` de l'hôte. **Proposé.**

## 12. Liste des tâches

### Préparation
- [x] Discuter le constat et lever les ambiguïtés avec Benoit (25 questions).
- [x] Déplacer les anciens documents dans `docs/old/`, sauf `docs/adr/`, et corriger les liens.
- [x] Rédiger ce plan et le nouvel index `docs/README.md`.
- [x] Écrire les ADR de la section 10, phase par phase, avant le code de chaque phase.
  - [x] Phase 1 : ADR 0043 à 0046 (2026-10-10).
  - [x] Phase 2 : ADR 0047 à 0049 (2026-10-10), avec leurs chantiers `rebuild/2-*`.
  - [x] Phase 3 : ADR 0050 à 0052 (2026-10-10), avec leurs chantiers `rebuild/3-*` (tableau dans l'ADR 0052).
  - [x] Phase 4 : ADR 0055 à 0058 (2026-10-10), avec leurs chantiers `rebuild/4-*` (tableau dans l'ADR 0058).

### Phase 1 — Fichiers
- [x] Créer les types `HackFile`, `HackDirectory`, `HackPath`, `HackFileInfo`, `HackDirectoryInfo`, `HackFileStream`, `HackFileSystemWatcher`.
- [x] Créer les exceptions `Hack*` et la traduction depuis `FileSystemErrorCode`.
- [x] Mettre en place le contexte d'exécution ambiant positionné par l'OS. Voir [`hack-file-api.md`](../../framework/hack-file-api.md).
- [x] Remplacer le magasin de paramètres séparé par des fichiers, avec migration des valeurs existantes ([`settings-files.md`](../../framework/settings-files.md)).
- [x] Implémenter les couches `/etc` puis `~/.config`, le verrou administrateur et la tolérance aux fichiers invalides ([`settings-files.md`](../../framework/settings-files.md)).
- [x] Créer l'API typée de paramètres et adapter l'app Paramètres ([`settings-files.md`](../../framework/settings-files.md)).
- [x] Écrire les journaux dans `/var/log` avec rotation, et adapter Error Log Viewer ([`logging.md`](../logging.md)).
- [x] Monter `/proc` et `/dev` en virtuel ([`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md)).
- [x] Tests de contrat pour chaque point (section « Tests » de chaque document).

### Phase 2 — Modèle d'app
- [x] Créer `AppDefinition` et l'attribut `[AppManifest]` ([`app-model.md`](../../framework/app-model.md)).
- [x] Créer la tâche MSBuild de génération de `manifest.json` par app, avec validation.
- [x] Créer la tâche d'agrégation `BuiltInApplications.json` et le `.targets` « une référence suffit ».
- [x] Migrer les apps existantes, puis supprimer `app.manifest.json` écrits à la main et `StaticManifest` ([`app-model.md`](../../framework/app-model.md#migration-des-apps)).
- [x] Implémenter la liste des apps système, la désinstallation machine et utilisateur, et la réinstallation.
  - [x] Liste des apps système : `AppCatalog.IsSystemApp`, `IsSystemApp="true"` sur les `ProjectReference` de l'hôte (`rebuild/2-system-apps`, [`app-model.md`](../../framework/app-model.md#apps-système-adr-0048-1)).
  - [x] Désinstallation machine et utilisateur, réinstallation (`rebuild/2-app-uninstall`, ADR 0048, [`app-installation.md`](../app-installation.md)).
- [x] Classer les capacités, créer la fenêtre d'approbation et la politique de sécurité.
  - [x] Classement des capacités, retrait de `settings.*` et `diagnostics.*`, seeder selon le statut système (`rebuild/2-capability-classification`, [`capabilities.md`](../../framework/capabilities.md)).
  - [x] Fenêtre d'approbation et politique de sécurité (`rebuild/2-dangerous-approval`, ADR 0049, [`dangerous-capability-approval.md`](../dangerous-capability-approval.md)).

### Phase 3 — Shell, commandes et corbeille
- [x] Étendre le parseur : pipes, redirections, `&&`, variables (`rebuild/3-shell-interpreter`, ADR 0050 et 0054, [`shell-interpreter.md`](../shell-interpreter.md)).
- [x] Ajouter répertoire courant et environnement par processus (`rebuild/3-process-environment`, ADR 0050 et 0053, [`process-environment.md`](../process-environment.md)).
- [x] Fusionner les 28 projets de commandes et rendre `cd`, `pwd`, `clear`, `help` normales.
  - [x] Fusion des 28 projets dans `HackerOs.Commands`, une seule `ProjectReference` dans l'hôte (`rebuild/3-commands-merge`, ADR 0051 §1, [`app-model.md`](../../framework/app-model.md#commandes-intégrées--un-seul-projet-adr-0051)).
  - [x] `cd`, `pwd`, `clear`, `help`, `sudo`, `export`, `unset`, `env` du catalogue, `IShellSession` et `shell.control`, plus aucun nom codé en dur dans le terminal (`rebuild/3-shell-commands`, ADR 0051 §2-5, [`shell-commands.md`](../shell-commands.md)).
- [x] Implémenter la corbeille et l'API `HackTrash` (`rebuild/3-trash`, ADR 0052, [`trash.md`](../trash.md)).

### Phase 4 — SDK complet et apps de base
- [x] Inventorier les services internes injectés par les apps système et ajouter leurs équivalents au SDK.
  - [x] `HackUser`, `HackEnvironment.GetFolderPath`, `HackApps`, `HackAppManagement` et l'agent d'authentification de l'OS ; FileExplorer, TextEditor et CodeEditor sortent de la liste d'exemptions (`rebuild/4-sdk-user-and-apps`, ADR 0055 §5 et 0056 §1-3, [`app-sdk.md`](../../framework/app-sdk.md), [`admin-authentication-agent.md`](../admin-authentication-agent.md)).
  - [x] `HackSettings.OpenSystemAsync`, `HackPermissionAdministration`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics` ; Paramètres et Diagnostic sortent de la liste d'exemptions, qui est vide (`rebuild/4-sdk-system-admin`, ADR 0056 §8-11 et 0060, [`system-administration.md`](../../framework/system-administration.md)).
- [x] Ajouter le test d'architecture qui interdit les références internes depuis `Apps/`.
  - [x] Vérification au build des références et des injections, test `IJSRuntime`, liste d'exemptions temporaire (`rebuild/4-sdk-boundary`, ADR 0055 et 0059, [`app-sdk.md`](../../framework/app-sdk.md)).
  - [x] Hôte de shell : `HackShell`, capacité `shell.host`, `TerminalPanel` sans `Platform.Core`, CodeEditor système (`rebuild/4-sdk-shell-host`, ADR 0056 §7, [`shell-host.md`](../../framework/shell-host.md)).
  - [x] Mécanisme d'exemption supprimé : toute violation de la frontière est une erreur, aucun projet ne peut s'y soustraire, toute la solution compile sans diagnostic de frontière (`rebuild/4-sdk-boundary-final`, ADR 0055 §6, [`app-sdk.md`](../../framework/app-sdk.md#fin-des-exemptions-temporaires-adr-0055-6)).
- [x] Explorateur : couper, copier, coller, corbeille, glisser-déposer (`rebuild/4-file-explorer`, ADR 0056 §12 et 0058 §1, [`file-explorer.md`](../file-explorer.md)).
  - [x] Presse-papiers de l'OS partagé avec l'hôte (texte, fichiers collés, réglage `clipboard.config`), Diagnostic sur `HackClipboard` (`rebuild/4-file-explorer`, ADR 0065, [`clipboard.md`](../clipboard.md)).
- [x] Gestionnaire de tâches : performance, disque, réseau, détail, filtre, sélection multiple, via `/proc` (`rebuild/4-proc-metrics` et `rebuild/4-task-manager`, ADR 0057 et 0058 §2, [`system-metrics.md`](../system-metrics.md), [`task-manager.md`](../task-manager.md)).
- [x] Éditeur : remplacer et tout remplacer (`rebuild/4-text-editor`, ADR 0058 §3, [`text-editor.md`](../text-editor.md)).
- [x] Console : complétion par Tab et onglets (`rebuild/4-terminal`, ADR 0058 §4, [`terminal-console.md`](../terminal-console.md)).

### Après la phase 4 (hors plan initial, ADR 0061 à 0065)
- [x] Terminal sur xterm.js, discipline de ligne en C#, écran alternatif (`rebuild/terminal-xterm`, PR #115, ADR 0061, [`terminal-emulator.md`](../terminal-emulator.md)).
- [x] Éditeur de code en atelier à la VS Code (`rebuild/code-editor-workspace` et `rebuild/code-editor-dock`, PR #117 à #119, ADR 0062, [`code-editor.md`](../code-editor.md)) et composants d'app intégrables, terminal embarqué par `IHackTerminal` (ADR 0063, [`embedded-components.md`](../../framework/embedded-components.md)).
- [x] Shell POSIX, scripts, jobs, apps lancées par leur nom, commandes Linux, `nano` (PR #120 et #121, ADR 0064, [`shell-interpreter.md`](../shell-interpreter.md), [`linux-commands.md`](../linux-commands.md)).
- [x] Presse-papiers de l'OS partagé avec l'hôte (PR #122, ADR 0065, [`clipboard.md`](../clipboard.md)) — voir la phase 4 ci-dessus.

### Vérification finale de la phase 4 (2026-10-16)

Repasse de `rebuild/4-sdk-boundary-final` sur `rebuild/base` après les PR #115
à #122, avant la fusion des phases 1 à 4 dans `main` (PR #114) :

- [x] Frontière du SDK : la solution compile sans aucun avertissement ni
  diagnostic `HOSAPP0002`, `HOSAPP0003`, `HOSAPP0030`, `HOSAPP0031` ou
  `HOSSDK0001` ; aucune référence de `Apps/` ou du SDK vers `Platform.*`,
  `Infrastructure.*` ou `Server.*` ; plus de mécanisme d'exemption ;
  `SdkBoundarySourceTests` couvre toutes les apps, nouvelles comprises.
- [x] Apps et composants ajoutés depuis #113 : SDK public seulement, capacités
  déclarées dans leur `[AppManifest]`, aucune injection de service interne.
- [x] Aucune API `[Obsolete]` utilisée dans `Apps/`.
- [x] Une dérive corrigée : la copie du terminal contournait le presse-papiers
  de l'OS et `clipboard.config` ; elle passe maintenant par le pont de l'ADR
  0065 ([`terminal-emulator.md`](../terminal-emulator.md)).
- [x] Toutes les suites `Tests/*.Tests` hors E2E passent ; documentation sans
  lien mort, index complets (`docs/README.md`, `docs/old/README.md`).

Détail : [`app-sdk.md`](../../framework/app-sdk.md#vérification-finale-de-la-phase-4-2026-10-16).

## 13. Références

- [`old/README.md`](../old/README.md) : index de l'ancienne documentation.
- [`old/virtual-filesystem.md`](../old/virtual-filesystem.md) : moteur de fichiers conservé.
- [`old/settings-system.md`](../old/settings-system.md) : système de paramètres actuel.
- [`old/app-contracts.md`](../old/app-contracts.md) et [`old/blazor-app-sdk.md`](../old/blazor-app-sdk.md) : contrats d'app actuels.
- [`old/ecosystem-maturity-gap-analysis.md`](../old/ecosystem-maturity-gap-analysis.md) : manques identifiés précédemment, dont l'isolation.
- [`adr/`](../adr/) : décisions d'architecture en vigueur.
- `src/` : legacy TypeScript, référence de comportement en lecture seule.
