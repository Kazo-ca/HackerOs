# ADR 0044 : Paramètres en fichiers, en couches `/etc` puis `~/.config`

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 1 — Fichiers ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.2, décisions D16 à D18).
- **Remplace :** ADR 0004 en entier. ADR 0011 en partie : sont remplacés le
  document canonique hors fichiers, la clé `SettingsDocumentKey` comme
  identité, la table des portées et chemins, et le rejet atomique d'une écriture
  invalide. La grammaire `.config` est conservée. ADR 0029 en entier : le domaine
  de synchronisation « Settings » est retiré.
- **Complète :** ADR 0030 (exclusion de `~/.local/state/`).
- **Valeurs par défaut retenues** (plan, section 11) : verrou administrateur par
  une section `[locked]`.

## Contexte

Aujourd'hui, les paramètres vivent dans le magasin IndexedDB `settings`
(`IndexedDbSettingsDocumentService`). Le montage `/etc/hackeros` qui devait les
projeter en fichiers (`SettingsFileSystemProvider`, `SettingsFileProjection`)
n'est jamais branché dans l'OS réel : `FileSystemMountRouter` est créé avec le
seul fournisseur IndexedDB (`OS/HackerOs.Ecosystem/EcosystemServiceCollectionExtensions.cs`).
`nano /etc/hackeros/...` ne voit donc rien. La sécurité vient du service de
paramètres (capacités `settings.*`, autorité minimale par document), et non des
droits du fichier.

Le plan veut l'inverse (D16) : le fichier est la seule source de vérité, et ses
droits décident de l'accès.

## Décision

### 1. Le fichier est la seule source de vérité

- Un paramètre est une ligne d'un fichier ordinaire, stocké par le fournisseur
  IndexedDB du système de fichiers comme n'importe quel fichier. Il n'y a ni
  montage spécial ni projection.
- Le magasin `settings`, `ISettingsDocumentService`,
  `IndexedDbSettingsDocumentService`, `InMemorySettingsDocumentService`,
  `SettingsFileProjection`, `SettingsFileSystemProvider`, `IAppSettingsGateway`
  (et `IAppExecutionContext.Settings`), `SettingsDocumentDefinition` et
  `SettingsDocumentKey` sont **retirés** du chemin d'exécution. Seule exception :
  la migration (section 8) relit encore le magasin.
- **L'accès est contrôlé uniquement par les droits du fichier**, à travers la
  passerelle fichier : capacités `filesystem.*` selon le chemin, autorité, bits
  de mode, tickets d'élévation de l'ADR 0042. Les capacités `settings.read`,
  `settings.write`, `settings.system.read` et `settings.system.write` ne sont plus
  évaluées nulle part. Elles restent dans le catalogue `AppCapabilities` jusqu'à
  l'ADR de phase 2 sur les permissions dangereuses, qui décidera de leur retrait.

### 2. Emplacements

`<appId>` est l'identifiant canonique du manifeste (par exemple
`org.hackeros.text-editor`). `hackeros` est réservé à l'OS. `<document>` vaut
`settings` par défaut.

| Couche | Chemin | Propriétaire et mode |
|---|---|---|
| Système (app) | `/etc/<appId>/<document>.config` | dossier `system:system 0755`, fichier `system:system 0644` |
| Système (OS) | `/etc/hackeros/<document>.config` | idem |
| Utilisateur (app) | `~/.config/<appId>/<document>.config` | dossier `<user>:<group> 0755`, fichier `0644` (umask de l'ADR 0043, protégé par `~` en `0700`) |
| Utilisateur (OS) | `~/.config/hackeros/<document>.config` | idem |
| État local, non synchronisé | `~/.local/state/hackeros/…` | idem |

Conséquences sur la résolution des capacités dans `FileSystemService.ResolveCapability`
(`Platform/HackerOs.Platform.Core/FileSystem/FileSystemService.cs`) :

- `~/.config/<appId>/` (lecture et écriture) correspond à
  `filesystem.private.read/write` de cette app. Il remplace l'actuel
  `~/.config/apps/<appId>/`, qui est déplacé par la migration.
- `/etc/<appId>/` en **lecture** correspond à `filesystem.private.read` de cette
  app : une app lit sa propre couche système sans la capacité large
  `filesystem.system.read`. L'**écriture** reste `filesystem.system.write`, avec
  autorité Administrateur et bits de mode. En pratique, il faut `sudo` ou un
  ticket d'approbation de l'ADR 0042, puisque le fichier appartient à `system`.
- Les autres chemins ne changent pas.

`HackSettings` ne crée **jamais** de fichier dans `/etc`. Un fichier système
absent est une couche vide. Le dossier `~/.config/<appId>/` et le fichier
utilisateur sont créés à la première écriture.

### 3. Couches et verrou (D18)

Valeur effective d'une clé = la première trouvée dans cet ordre :

1. clé **verrouillée** dans la couche système → valeur système (ou valeur par
   défaut si elle n'a pas de valeur système) ; la couche utilisateur est ignorée
   pour cette clé ;
2. couche utilisateur `~/.config/<appId>/<document>.config` ;
3. couche système `/etc/<appId>/<document>.config` ;
4. valeur par défaut déclarée par l'app.

**Syntaxe du verrou (valeur retenue, plan section 11) :** une section réservée
`[locked]` dans le fichier **système**. Chaque ligne y nomme une clé qualifiée
(`clé` ou `Groupe.clé`, comme `SettingFieldDeclaration.QualifiedKey`) avec la
valeur `true` :

```text
# /etc/org.hackeros.text-editor/settings.config
theme=dark

[Editor]
tab-size=4

[locked]
theme=true
Editor.tab-size=true
```

- `false` ou l'absence de la clé : pas de verrou. Toute autre valeur est
  invalide : ignorée, avec un avertissement.
- Une section `[locked]` dans un fichier **utilisateur** est ignorée, avec un
  avertissement.
- `locked` devient un nom de groupe réservé. Un schéma d'app qui le déclare est
  refusé par `AppManifestValidator`.

### 4. Format

La grammaire `.config` de l'ADR 0011 est conservée telle quelle (`#`
commentaire, `[Groupe]`, `clé=valeur`, échappements `\#`, `\=`, `\\`, `\n`, `\r`,
`\t`), ainsi que l'analyseur `ConfigDocumentFormat`
(`Shared/HackerOs.Simulation.Abstractions/Settings/`). `schemaVersion` reste une
clé de racine facultative.

### 5. Fichier invalide (D17)

- **L'écriture est toujours acceptée**, que ce soit par `nano`, l'app Paramètres
  ou `HackFile` : c'est une écriture de fichier ordinaire. Il n'y a plus de
  validation à l'écriture.
- **À la lecture**, chaque problème est traité ligne par ligne, et jamais en
  rejetant tout le document :
  - ligne mal formée : ignorée ;
  - clé non déclarée : ignorée, mais conservée dans le fichier ;
  - valeur qui ne respecte pas son type ou ses valeurs permises : ignorée, et la
    couche suivante s'applique ;
  - clé en double : la dernière l'emporte.
- Chaque problème produit **un** avertissement par fichier et par révision, dans
  `/var/log/apps/<appId>.log` (ou `/var/log/syslog` pour `hackeros`), selon
  l'ADR 0045, au format :
  `settings: /etc/x/settings.config:12: invalid value for 'tab-size' (expected int64), using default`.

### 6. Moteur de fusion commun

Une fonction pure, partagée par le SDK et la plateforme, dans
`Shared/HackerOs.Simulation.Abstractions/Settings/LayeredConfigResolver.cs` :

```csharp
public static class LayeredConfigResolver
{
    public static LayeredConfigSnapshot Resolve(
        SettingsSchema schema,
        string? systemContent,   // null = fichier absent
        string? userContent,
        string systemPath,
        string userPath);
}

public sealed record LayeredConfigSnapshot(
    IReadOnlyDictionary<string, string> Values,          // clé qualifiée -> valeur effective sérialisée
    IReadOnlyDictionary<string, SettingLayer> Sources,
    IReadOnlySet<string> LockedKeys,
    IReadOnlyList<LayeredConfigWarning> Warnings);

public enum SettingLayer { Default, System, User }
public sealed record LayeredConfigWarning(string Path, int LineNumber, string QualifiedKey, string Message);
```

`SettingsSchema` et `SettingFieldDeclaration` existants restent le modèle de
schéma (types `String`, `Integer`, `Boolean`, `Enum`).

### 7. API typée `HackSettings` (SDK)

Dossier `Shared/HackerOs.AppSdk/Settings/`, namespace public
**`HackerOs.AppSdk.Settings`**. Elle s'appuie sur l'API `Hack*` de l'ADR 0043 et
sur le contexte ambiant, donc elle passe par la passerelle fichier. Le schéma
vient de `AppManifest.Settings` en phase 1, puis de la classe `[AppManifest]` en
phase 2, sans changement d'API.

```csharp
public static class HackSettings
{
    // Ouvre le document de l'app appelante (schéma = manifeste).
    public static ValueTask<HackSettingsDocument> OpenAsync(
        string documentId = "settings", CancellationToken ct = default);

    // Raccourcis sur le document par défaut.
    public static ValueTask<T> GetAsync<T>(string qualifiedKey, CancellationToken ct = default);
    public static ValueTask SetAsync<T>(string qualifiedKey, T value, CancellationToken ct = default);
}

public sealed class HackSettingsDocument : IAsyncDisposable
{
    public string SystemPath { get; }          // /etc/<appId>/<doc>.config
    public string UserPath { get; }            // /home/<user>/.config/<appId>/<doc>.config
    public T Get<T>(string qualifiedKey);      // string, long, int, bool, enum C#
    public SettingLayer GetSource(string qualifiedKey);
    public bool IsLocked(string qualifiedKey);
    public IReadOnlyList<LayeredConfigWarning> Warnings { get; }
    public ValueTask SetAsync<T>(string qualifiedKey, T value,
        SettingLayer target = SettingLayer.User, CancellationToken ct = default);
    public ValueTask ResetAsync(string qualifiedKey,
        SettingLayer target = SettingLayer.User, CancellationToken ct = default);
    public event EventHandler<HackSettingsChangedEventArgs>? Changed;
}

public sealed class HackSettingLockedException : InvalidOperationException;
```

- Une clé **non déclarée** dans le schéma lève `ArgumentException` : seules les
  clés déclarées sont typées, documentées et affichables par l'app Paramètres.
- `SetAsync` vers la couche utilisateur d'une clé verrouillée lève
  `HackSettingLockedException`. Vers la couche système, il faut avoir le droit
  d'écrire `/etc/<appId>/` ; sinon `HackUnauthorizedAccessException`, ce qui
  déclenche l'invite d'approbation administrateur de l'ADR 0042.
- L'écriture modifie la ligne en conservant commentaires, ordre et clés
  inconnues (`ConfigDocumentFormat`). Elle passe par `HackFile` avec la règle
  d'un seul nouvel essai sur conflit (ADR 0043).
- `Changed` est alimenté par deux `HackFileSystemWatcher`, un sur chaque fichier.
  Un `nano /etc/...` ou une modification depuis l'app Paramètres est donc vu par
  l'app en cours d'exécution.

### 8. Paramètres de l'OS

Les services de la plateforme utilisent le même moteur à travers
`IOsSettingsService` (`Platform/HackerOs.Platform.Core/Settings/OsSettingsService.cs`),
qui lit et écrit les fichiers `hackeros` avec le contexte de l'utilisateur de
la session :

| Document actuel (magasin IndexedDB) | Nouveau(x) fichier(s) | Couches |
|---|---|---|
| `/etc/hackeros/appearance.json` | `/etc/hackeros/appearance.config` + `~/.config/hackeros/appearance.config` | système + utilisateur |
| `/etc/hackeros/ui-platform-preference.json` | `/etc/hackeros/ui-platform-preference.config` + `~/.config/hackeros/ui-platform-preference.config` | système + utilisateur |
| `/etc/hackeros/policy.config` (`PolicySettingsDocuments`) | `/etc/hackeros/policy.config` | système seulement (couche utilisateur ignorée) |
| `/etc/hackeros/file-associations.json` | `/etc/hackeros/file-associations.json`, JSON strict, document entier | système seulement ; une couche utilisateur pourra venir avec l'ADR de phase 2 sur les apps |
| `/etc/hackeros/start-menu.json` (un `profiles` par utilisateur) | `~/.config/hackeros/start-menu.json`, un par utilisateur, JSON strict | utilisateur seulement |
| géométrie des fenêtres (`WindowGeometrySettings`, portée `AppUserDevice`) | `~/.local/state/hackeros/windows/<appId>.config` | utilisateur seulement, non synchronisé |

Les documents JSON enregistrés restent des exceptions de format, déjà admises
par l'ADR 0011. Ils ne sont pas fusionnés en couches. Leur service propriétaire
ignore un fichier invalide, garde ses valeurs par défaut et écrit un
avertissement, comme en section 5.

Les portées `AppDevice`, `AppUserDevice` et `AppRoamingUser` disparaissent : une
installation est une machine, et `/etc` en est la couche.

### 9. Migration au premier démarrage

Service `SettingsStoreToFilesMigration` (`Platform/HackerOs.Platform.Core/Settings/`) :

1. **Au démarrage**, après l'amorçage du système de fichiers et avant toute
   connexion, avec un contexte système : chaque enregistrement `OsAdmin` du
   magasin `settings` est écrit dans son fichier système (tableau de la
   section 8). Les documents JSON plats sont convertis en `.config`. Les valeurs
   d'apparence et de plateforme d'interface vont dans la couche **système**, ce
   qui conserve le comportement actuel, valable pour toute la machine. Marqueur :
   `/var/lib/hackeros/migrations/settings-to-files` (fichier vide).
2. **À la connexion de chaque utilisateur**, après `FileSystemSeeder.SeedAsync` :
   ses documents d'app (`AppUser`, `AppRoamingUser`, `AppUserDevice`) sont écrits
   dans `~/.config/<appId>/…`, son profil `start-menu` dans
   `~/.config/hackeros/start-menu.json`, sa géométrie de fenêtres dans
   `~/.local/state/hackeros/windows/`, et l'ancien dossier `~/.config/apps/<appId>/`
   est déplacé vers `~/.config/<appId>/`. Marqueur :
   `~/.local/state/hackeros/migrations/settings-to-files`.
3. Un fichier cible qui existe déjà n'est jamais écrasé.
4. **Le magasin IndexedDB `settings` n'est ni supprimé ni vidé, et la version de
   schéma IndexedDB ne change pas** dans cette phase (voir
   [`old/common-pitfalls.md`](../old/common-pitfalls.md) sur le versionnage
   IndexedDB). Son retrait fera l'objet d'une étape ultérieure.

`FileSystemSeeder` crée en plus `/etc/hackeros` (`0755`), `/var/lib/hackeros`
(`0755`), `~/.local`, `~/.local/state` et `~/.local/state/hackeros` (`0755`). Il
ne crée plus `~/.config/apps`.

### 10. Synchronisation

- Le domaine de synchronisation « Settings » de l'ADR 0029
  (`SettingsSyncService`, `ISettingsSyncService`, `SyncEligible`) est retiré.
- La couche utilisateur se trouve sous `/home/<user>`. Elle est donc déjà
  synchronisée par le domaine « FileSystem » de l'ADR 0030, ce qui couvre
  `~/.config/hackeros/appearance.config`, le seul document synchronisé
  jusqu'ici.
- `/etc` n'est pas synchronisé, comme les documents administrateur de l'ADR 0029.
- **Complément à l'ADR 0030 :** le parcours de synchronisation exclut
  `/home/<user>/.local/state/` (état propre à l'appareil, au sens XDG).

### 11. App Paramètres

L'app Paramètres (`Apps/System/HackerOs.Apps.Settings`) lit et écrit par
`HackSettings` et `IOsSettingsService`. Elle affiche pour chaque clé sa couche
d'origine et un cadenas si la clé est verrouillée. Une modification système passe
par l'invite d'approbation de l'ADR 0042.

## Conséquences

- `nano /etc/hackeros/appearance.config` et l'app Paramètres modifient le même
  fichier. Aucune projection ne peut se désynchroniser.
- Un fichier mal édité ne casse plus rien : l'app démarre avec ses valeurs par
  défaut et le journal dit pourquoi. En contrepartie, une faute de frappe n'est
  signalée qu'au moment de la lecture.
- Un administrateur impose une valeur à tous les utilisateurs avec `[locked]`.
- Les tests qui dépendent de `ISettingsDocumentService`, de
  `SettingsFileSystemProvider` ou de `SettingsSyncService` sont réécrits ou
  supprimés dans la même phase.
- Les documents qui suivaient les portées « appareil » et « itinérant » perdent
  cette distinction. Elle n'avait qu'un seul utilisateur réel (la géométrie des
  fenêtres), qui passe dans `~/.local/state`.

## Références

- ADR 0004 (remplacé), ADR 0011 (remplacé en partie), ADR 0029 (remplacé), ADR 0030 (complété).
- ADR 0042 : invite d'approbation administrateur.
- ADR 0043 : API `Hack*` et contexte ambiant.
- ADR 0045 : journaux en fichiers.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 5.2 et 11.
- [`old/settings-system.md`](../old/settings-system.md) : système actuel.
