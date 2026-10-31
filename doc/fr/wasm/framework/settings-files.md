> [English version](../../../en/wasm/framework/settings-files.md)

# Paramètres en fichiers

L'accueil mobile utilise `~/Desktop/.mobile/layout.conf` (sections et clés texte,
ADR 0166 ; migration en lecture de l'ancien JSON) pour les pages
et le dock. Le document OS à couches `mobile.config` expose `mouseHelpers`
(défaut `true`) à Paramètres par `HackSettings.OpenSystemAsync(AppContext, "mobile")`.
Voir [mobile-home-screen.md](../dev/mobile-home-screen.md) et les ADR 0165/0166.
`OsSettingsFile` peut désormais utiliser `OsSettingsLocation.UserHome` pour
résoudre un chemin sous le dossier personnel de la session ; les accès restent
ceux de cet utilisateur, comme pour `UserConfig` et `UserState`.

Mise en œuvre de la section 5.2 du [plan de refonte](../dev/plans/ecosystem-rebuild-plan.md)
selon l'[ADR 0044](../dev/adr/0044-settings-as-files.md). Ce document décrit le code
livré ; l'ADR reste la référence des décisions.

## Objectif

Les paramètres des apps et de l'OS sont de vrais fichiers texte du système de
fichiers simulé. L'app Paramètres, une app tierce et `nano /etc/...` lisent et
modifient les mêmes fichiers, et seuls les droits du fichier décident qui peut
écrire. Le magasin IndexedDB `settings` n'est plus utilisé que par la
migration, en lecture seule.

## Emplacements

| Contenu | Couche système | Couche utilisateur |
| --- | --- | --- |
| Réglages d'une app | `/etc/<appId>/<document>.config` | `~/.config/<appId>/<document>.config` |
| Réglages de l'OS (apparence, interface, menu Démarrer) | `/etc/hackeros/<document>.config` | `~/.config/hackeros/<document>.config` |
| Politique (`policy.config`) | `/etc/hackeros/policy.config` | aucune (document `SystemOnly`) |
| Politique des capacités dangereuses (`security.config`, ADR 0049) | `/etc/hackeros/security.config` | aucune (document `SystemOnly`) |
| OOM killer (`[oom-killer]` : `enabled=false`, `min-free-percent=5`, ADR 0126, voir [`oom-killer.md`](../dev/oom-killer.md)) | `/etc/hackeros/memory.config` | aucune (couche système seulement) |
| Fournisseur du contrôle de fichiers et des dialogues de fichiers (`[providers]`, clé = contrat, valeur = app ; absent = explorateur ; ADR 0156 et 0158, voir [`embedded-components.md`](embedded-components.md)) | `/etc/hackeros/components.config` (lu ligne par ligne, sans schéma) | aucune |
| Entrées que les apps ajoutent aux menus des fichiers (un JSON par app, écrit par l'OS au démarrage et à chaque changement d'installation ; ADR 0151, 0159) | `/etc/hackeros/context-menus.d/<appId>.json` | aucune |
| Entrées des menus des fichiers masquées par l'utilisateur (`[hidden]`, `<appId>.<name>=true` ; Paramètres ▸ Menus des fichiers ; ADR 0159 §8) | `~/.config/hackeros/context-menus.config` (lu ligne par ligne à chaque ouverture d'un menu) | aucune |
| Décisions sur les capacités dangereuses (ADR 0049) | `/etc/hackeros/app-permissions.config` (`allow`/`deny`) | `~/.config/hackeros/app-permissions.config` (`deny` seulement) |
| Accessibilité (`highContrast`, `textScale`, `reducedMotion`, ADR 0105, voir [`accessibility.md`](../dev/accessibility.md)) | `/etc/hackeros/accessibility.config` (aussi lu par l'écran de connexion) | `~/.config/hackeros/accessibility.config` |
| Date et heure (`timeZone` : `auto` ou un fuseau IANA, ADR 0193, voir [`time-zone.md`](time-zone.md)) | `/etc/hackeros/datetime.config` (aussi lu par l'écran de connexion) | `~/.config/hackeros/datetime.config` |
| Sons du système (`enabled`, `theme`, `volume`, ADR 0204, voir [`system-sounds.md`](../dev/system-sounds.md)) | `/etc/hackeros/sound.config` | `~/.config/hackeros/sound.config` |
| Associations de fichiers | `/etc/hackeros/file-associations.json` | `~/.config/hackeros/file-associations.json` (même schéma, lu avant le fichier de la machine, ADR 0121, [`user-file-associations.md`](../dev/user-file-associations.md)) |
| Raccourcis du gestionnaire de fenêtres (`[linux]`, `[fallback]`, ADR 0095, voir [`window-model.md`](window-model.md)) | `/etc/hackeros/keybindings.config` | `~/.config/hackeros/keybindings.config` |
| Boîte « ne répond pas » (`[unresponsive] dialog`, ADR 0127 §3, voir [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md)) | `/etc/hackeros/window-manager.config` | `~/.config/hackeros/window-manager.config` |
| Taille des fenêtres (`width`, `height`, `maximized`, ADR 0116 et 0122, voir [`window-geometry.md`](../dev/window-geometry.md)) | aucune | `~/.local/state/hackeros/windows/<appId>.config` |
| Apps désinstallées (ADR 0048, voir [`app-installation.md`](../dev/app-installation.md)) | `/etc/hackeros/apps.config` | `~/.config/hackeros/apps.config` |
| Partage du presse-papiers avec l'hôte (clé `sharing`, ADR 0065, voir [`clipboard.md`](../dev/clipboard.md)) | `/etc/hackeros/clipboard.config` | `~/.config/hackeros/clipboard.config` |
| Synchro périodique (ADR 0101 §4, voir [`synchronization.md`](../dev/synchronization.md)) | `/etc/hackeros/sync.config` (`intervalMinutes`, lu seulement ici) | `~/.config/hackeros/sync.config` (`paused`) |
| Conflits de synchro en attente (ADR 0101 §2) | aucune | `~/.local/state/hackeros/sync-conflicts.json` |
| Bureau (`[view]` taille, réorganisation, tri, icônes système ; `[positions]` case de chaque icône ; lu et écrit par `HackDesktopSettings`, sans schéma ; ADR 0152, 0160, voir [`desktop.md`](../dev/desktop.md)) | aucune | `~/.config/hackeros/desktop.config` |

`~/.local/state/` contient l'état propre à l'appareil : la synchronisation du
système de fichiers l'ignore (`IFileSystemSyncService.IsDeviceLocal`).

Au premier démarrage, `/etc/hackeros/` reçoit des fichiers par défaut
commentés (mode `0644`, propriétaire `root`). Comme ces fichiers listent toutes
les clés, l'origine affichée d'une valeur de l'OS est « système » et non
« intégrée » tant que personne n'a rien changé.

### Clés sensibles (ADR 0100)

Un document qui déclare des clés non publiques (`SettingSensitivity` `Private`,
`SecretReference` ou `Restricted`) range ces clés dans un fichier compagnon
`<doc>.private.config` en mode `0600`, à côté de `<doc>.config`, dans chaque
couche. Quand l'appelant ne peut pas lire le fichier privé, la clé est rendue
« masquée » au lieu de faire échouer la lecture. Les secrets vont dans le
trousseau de connexion. Les documents de l'OS n'acceptent que des clés
publiques. Détails : [`sensitive-settings.md`](../dev/sensitive-settings.md).

## Architecture

| Couche | Type | Rôle |
| --- | --- | --- |
| Simulation.Abstractions | `ConfigDocumentLines`, `ConfigDocumentEditor` | Lecture ligne par ligne du format `.config` (ADR 0011) et écriture qui ne touche qu'une ligne, en gardant commentaires, lignes vides et clés inconnues. |
| Simulation.Abstractions | `LayeredConfigResolver`, `LayeredConfigSnapshot` | Fusion des couches, verrous, avertissements `settings: <chemin>:<ligne>: <message>`. |
| Simulation.Abstractions | `SettingsTextFile`, `SettingsFilesWatcher` | Lecture, écriture (création des dossiers manquants) et surveillance des deux fichiers d'un document par une passerelle de fichiers. |
| Simulation.Abstractions | `ILegacySettingsStore`, `LegacySettingsRecord` | Contrat de lecture de l'ancien magasin, pour la migration. |
| AppSdk | `HackSettings`, `HackSettingsDocument` | API typée des apps. |
| AppSdk.Blazor | `SettingOriginBadge` | Badge réutilisable « à vous », « défaut système », « intégré », « verrouillé par l'admin ». |
| Platform.Core | `IOsSettingsService`, `OsSettingsService` | Documents de l'OS (`OsSettingsDocument`) et fichiers entiers (`OsSettingsFile`), pour la session courante. |
| Platform.Core | `SettingsStoreToFilesMigration` | Migration unique depuis IndexedDB, puis fichiers par défaut. |
| Infrastructure.Browser | `IndexedDbLegacySettingsStore` | Lecture `getAll` du magasin `settings`, sans changer la version du schéma. |
| Platform.Blazor | `WindowGeometryPersistence`, `WindowGeometryRecorder` | Lit et écrit la taille et l'état agrandi des fenêtres (ADR 0116, ADR 0122) : écrite par le recorder sur les faits de `WindowRuntime`, relue par `WindowLaunchCoordinator` à l'ouverture. |

### Ordre des couches

Pour chaque clé déclarée :

1. clé verrouillée dans la section `[locked]` du fichier système : valeur
   système, sinon valeur par défaut ;
2. sinon valeur utilisateur valide ;
3. sinon valeur système valide ;
4. sinon valeur par défaut du schéma.

`[locked]` n'est lu que dans le fichier système ; dans un fichier utilisateur
la section est ignorée avec un avertissement. `locked` est donc un nom de
groupe réservé, refusé par le schéma et le validateur de manifeste.

### Analyse tolérante

Une ligne mal formée, une clé inconnue ou une valeur invalide est ignorée ; la
couche suivante s'applique. Chaque problème donne un avertissement envoyé à
`IAppLoggingGateway` (pour une app) ou à `ILogger` (pour l'OS), une seule fois
par révision de fichier. L'écriture dans `/var/log` relève du chantier des
journaux (ADR 0045).

### Droits

| Opération | Capacité de l'app | Droits du fichier |
| --- | --- | --- |
| Lire `~/.config/<appId>/` | `filesystem.private.read` | ceux de l'utilisateur |
| Écrire `~/.config/<appId>/` | `filesystem.private.write` | ceux de l'utilisateur |
| Lire `/etc/<appId>/` | `filesystem.private.read` | `0644` : lisible par tous |
| Écrire `/etc/...` | `filesystem.system.write` | autorité Administrateur, bits de mode, ticket d'approbation de l'ADR 0042 consommé à chaque appel |

Une autre app n'a aucun accès au dossier privé `~/.config/<appId>/` d'une app.

## Utilisation

### Dans une app

Le schéma vient de `AppManifest.Settings` (clés, types, valeurs par défaut,
groupes). Chaque méthode existe avec le contexte de l'app en paramètre, et sans
lui : elle utilise alors le contexte ambiant de l'ADR 0043
(`HackExecutionContext.Current`), comme l'API de fichiers `Hack*`.

```csharp
await using HackSettingsDocument settings = await HackSettings.OpenAsync(AppContext);
EditorTheme theme = settings.Get<EditorTheme>("theme");      // enum : "high-contrast" <-> HighContrast
bool locked = settings.IsLocked("autosave");
SettingLayer source = settings.GetSource("theme");          // Default, System ou User
settings.Changed += (_, e) => Refresh(e.ChangedKeys);       // édition externe (nano, autre fenêtre)
await settings.SetAsync("Editor.tab-size", 2);              // couche utilisateur
await settings.ResetAsync("theme");                         // retire la valeur utilisateur

int size = await HackSettings.GetAsync<int>(AppContext, "Editor.tab-size"); // lecture ponctuelle
int same = await HackSettings.GetAsync<int>("Editor.tab-size");             // contexte ambiant
```

- Une clé non déclarée ou un type incompatible lève `ArgumentException`.
- Écrire une clé verrouillée lève `HackSettingLockedException` (avec le chemin
  du fichier système).
- Une erreur de fichier est traduite en `UnauthorizedAccessException`,
  `FileNotFoundException` ou `IOException`.
- `SetAsync(key, value, SettingLayer.System)` écrit `/etc/<appId>/` si l'app a
  la capacité et l'utilisateur les droits du fichier.

Plusieurs clés d'une même couche s'écrivent en une seule édition du fichier
avec `SetManyAsync(values, layer)` (une seule approbation pour `/etc`).

#### Documents de l'OS (`HackSettings.OpenSystemAsync`, ADR 0056 §8)

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync(AppContext, "appearance");
string accent = appearance.Get<string>("accent");
await appearance.SetAsync("accent", "cyan");                       // ~/.config/hackeros/appearance.config
await appearance.SetManyAsync(values, SettingLayer.System);        // /etc/hackeros : sudo ou approbation
```

- Documents ouverts : `appearance`, `accessibility` (ADR 0105), `ui-platform-preference`, `security`,
  `policy`, `clipboard` (ADR 0065), `screens` (ADR 0179 §7, Paramètres ▸ Écrans) (`OsSettingsDocumentRegistry`, Platform Core). Un autre id lève
  `ArgumentException`.
- Dans `appearance`, `desktopThemeId` et `mobileThemeId` sont des chaînes
  libres depuis `v3/11` (ADR 0103) : l'id d'un thème intégré ou d'un fichier
  `.theme` de `/etc/hackeros/themes/` ou `~/.config/hackeros/themes/` ; un id
  inconnu donne le thème par défaut (voir [`theme-editor.md`](../dev/theme-editor.md)).
- Dans `appearance`, `wallpaper`, `wallpaperFit` et `wallpaperScreen2` à
  `wallpaperScreen9` choisissent le fond d'écran du bureau, `mobileWallpaper`
  et `mobileWallpaperFit` celui de l'écran d'accueil mobile (ADR 0202, ADR 0217, voir
  [`wallpapers.md`](../dev/wallpapers.md)).
- Aucune capacité : les droits des fichiers décident (D16). La couche
  utilisateur est écrite par l'OS au nom de l'utilisateur de la session ; la
  couche système avec les droits de fichiers de l'app appelante, donc `sudo`
  ou l'approbation de l'ADR 0042.
- `Changed` suit `DocumentChanged` de `IOsSettingsService`, dans le contexte
  ambiant de l'app.

`IAppExecutionContext.Settings` (l'ancienne passerelle `IAppSettingsGateway`)
est retiré ; `IAppExecutionContext.HomeDirectory` donne le dossier personnel.

### Dans l'OS

`IOsSettingsService` lit un `OsSettingsDocument` (`ReadAsync` donne un
`LayeredConfigSnapshot`), écrit une ou plusieurs clés en une seule édition
(`SetAsync`, `SetManyAsync`), retire une clé (`ResetAsync`), lit et écrit des
fichiers entiers (`ReadFileAsync`, `ReadValidatedFileAsync`, `WriteFileAsync`)
et publie `DocumentChanged`. Une écriture système passe le `writer` de l'app
appelante, pour que ses droits s'appliquent. Les documents de l'OS sont
déclarés dans `AppearanceSettingsDocuments`, `UiPlatformPreferenceSettingsDocuments`,
`StartMenuSettingsDocuments`, `PolicySettingsDocuments` et
`FileAssociationSettingsDocuments`.

### App Paramètres

L'app propose deux cibles : « Moi seulement » (couche utilisateur) et « Tous
les utilisateurs (défaut système) ». Chaque réglage affiche son origine ; un
réglage verrouillé est désactivé pour la cible utilisateur. Écrire la couche
système demande l'approbation administrateur ; un refus affiche un message au
lieu d'une erreur. L'apparence passe par `HackSettings.OpenSystemAsync`
(chantier `4-sdk-system-admin`). « Synchroniser » pousse et tire le système de
fichiers, tire les permissions, pousse et tire les associations, puis relit
l'apparence. L'onglet « Synchronization » (ADR 0101) montre l'état de la
synchro périodique, la suspend pour l'utilisateur (`paused` dans
`~/.config/hackeros/sync.config`), lance un passage et liste les conflits en
attente avec un bouton qui ouvre le dialogue de conflit. L'onglet « Installed Apps » écrit les fichiers `apps.config`
(voir [`app-installation.md`](../dev/app-installation.md)).

L'onglet « Langue » (ADR 0104, `LanguagePanel`) n'utilise pas `HackSettings` :
la locale est un fichier systemd. Il montre la langue, `LANG`, les
surcharges `LC_MESSAGES`/`LC_ALL` et un exemple de formats de la session (lus
dans l'environnement du processus Paramètres), puis propose « Suivre la langue
du système » ou une des `HackLocale.AvailableLocales`. Choisir une locale écrit
`LANG=<locale>` dans `~/.config/locale.conf` (`HackLocale.FormatLocaleConf`,
`~/.config` créé au besoin) ; « Suivre la langue du système » retire `LANG` et
supprime le fichier s'il ne contient rien d'autre (un `LC_TIME` est gardé).
Le changement s'applique à la prochaine connexion, ce que le panneau affiche.
`/etc/locale.conf` est montré en lecture seule avec l'indication
`sudo localectl set-locale`. L'app déclare pour cela
`filesystem.user-home.read` et `filesystem.user-home.write`. Il n'y a pas de
bouton « Se déconnecter » : le SDK n'expose pas la déconnexion aux apps.
Tests : `Tests/HackerOs.Apps.Settings.Tests/LanguagePanelTests.cs`.

## Migration

`SettingsStoreToFilesMigration` lit l'ancien magasin une seule fois :

- au démarrage, les documents de l'OS (`OsAdmin`) deviennent des fichiers de
  `/etc/hackeros/`. Un document invalide est remplacé par la valeur par
  défaut. Le marqueur `/var/lib/hackeros/migrations/settings-to-files` empêche
  une seconde copie, donc les modifications de l'administrateur survivent aux
  redémarrages ;
- à la connexion, seules les valeurs de l'utilisateur connecté sont copiées :
  réglages d'app vers `~/.config/<appId>/`, épingles du menu Démarrer vers
  `~/.config/hackeros/start-menu.json`, géométrie vers `~/.local/state/`. Les
  anciens dossiers `~/.config/apps/<appId>` sont déplacés vers
  `~/.config/<appId>`. Le marqueur
  `~/.local/state/hackeros/migrations/settings-to-files` évite d'écraser ce
  que l'utilisateur a changé depuis.

Une erreur de lecture du magasin est journalisée et n'empêche ni le démarrage
ni la connexion. Le schéma IndexedDB ne change pas.

## Décisions de mise en œuvre

- Une écriture de plusieurs clés (`SetManyAsync`) se fait en une seule édition
  du fichier, pour qu'un observateur ne voie jamais un état à moitié écrit.
- Une app peut créer `~/.config/<appId>/` sans droit de lecture sur
  `~/.config` : si l'inspection d'un dossier parent est refusée, on le suppose
  existant et c'est la création du dossier final qui décide.
- La synchronisation des paramètres (`ISettingsSyncService`) disparaît : les
  fichiers sont synchronisés avec le reste du système de fichiers (l'ADR 0044
  remplace l'ADR 0029).
- Coût d'une écriture : les réglages passent par le système de fichiers comme
  tout fichier. Depuis l'ADR 0090, chaque chemin est recherché en une requête à
  IndexedDB ; enregistrer le choix Mobile/Desktop coûte 51 transactions
  (235 ms) au lieu de 604 (845 ms). Voir
  [`filesystem-path-lookup.md`](../dev/filesystem-path-lookup.md).

## Tests de contrat

Dans `Tests/HackerOs.Platform.Core.Tests/Settings/` :

- `LayeredConfigResolverTests` : couches, verrous, analyse tolérante, texte
  des avertissements, groupe réservé ;
- `ConfigDocumentEditorTests` : écriture qui préserve le texte humain ;
- `HackSettingsTests` : API du SDK sur le vrai système de fichiers, propriétaire
  et mode, verrou, réinitialisation, avertissements, droits sur `/etc`,
  isolement entre apps, notification de changement ;
- `SettingsStoreToFilesMigrationTests` : fichiers par défaut, copie unique,
  copie par utilisateur, magasin illisible.
- `SensitiveSettingsTests` : fichiers privés, masquage, clés mal placées,
  `Restricted`, secrets dans le trousseau (ADR 0100).

## Liste des tâches

- [x] Moteur de couches, verrous et analyse tolérante
- [x] API typée `HackSettings` dans le SDK
- [x] Service `IOsSettingsService` et documents de l'OS en fichiers
- [x] Géométrie des fenêtres dans `~/.local/state`, exclue de la synchronisation
- [x] Migration depuis le magasin IndexedDB
- [x] App Paramètres : cible de couche, origine et verrou par réglage
- [x] Retrait de `IAppSettingsGateway`, de la projection et de la synchronisation des paramètres
- [x] Tests de contrat
