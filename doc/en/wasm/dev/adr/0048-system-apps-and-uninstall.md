# ADR 0048 : Apps système, désinstallation machine ou utilisateur, et réinstallation

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 2 — Modèle d'app ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 6.3, décisions D9 à D11).
- **Complète :** ADR 0032. Le registre `AppEnablementRegistry` et le blocage du
  lancement sont réutilisés ; la notion « app désactivée » est remplacée par « app
  désinstallée », et sa source de vérité passe du magasin IndexedDB à deux
  fichiers.
- **Remplace en partie :** ADR 0033, pour le seul domaine de synchronisation
  « AppCatalog » (le domaine « FileAssociations » n'est pas touché).
- **Valeur par défaut retenue** (plan, section 11) : liste des apps système par la
  métadonnée `IsSystemApp="true"` sur la `ProjectReference` de l'hôte.

## Contexte

Aujourd'hui, rien ne distingue une app système d'une autre : l'ADR 0002 demande
que ce statut vienne d'une politique de confiance, mais aucun code ne le porte.
L'ADR 0032 a rendu la désactivation réelle (onglet « Installed Apps » de l'app
Paramètres, état `IsEnabled` dans `IPersistentAppCatalogRepository`), mais :

- l'état est un drapeau IndexedDB, invisible depuis le shell, contraire au
  principe « tout est fichier » ;
- il vaut pour toute la machine, sans portée utilisateur ;
- une app désactivée reste visible dans le lanceur, la recherche et les
  associations de fichiers ;
- n'importe quelle app, y compris le terminal ou les Paramètres, peut être
  désactivée.

## Décision

### 1. Liste de confiance des apps système (D9)

- L'hôte marque ses apps système sur la `ProjectReference` :

  ```xml
  <ProjectReference Include="..\..\Apps\System\HackerOs.Apps.Terminal\HackerOs.Apps.Terminal.csproj"
                    IsSystemApp="true" />
  ```

- L'agrégation de l'ADR 0047 recopie cette valeur dans le champ `isSystemApp` de
  `BuiltInApplications.json`. Elle s'applique à **toutes** les apps du projet
  référencé.
- **Une app ne peut pas se déclarer système.** `AppDefinition` n'a pas de
  propriété pour cela, et le schéma de `manifest.json` n'a pas de champ. Seul
  l'hôte, qui est du code de confiance compilé, écrit `isSystemApp`. Une app
  installée au runtime par le futur magasin sera toujours non système.
- Au runtime, `AppCatalog` expose `IsSystemApp(string appId)` et
  `SystemAppIds`, alimentés par `BuiltInApplications.json`.

**Apps système de l'hôte Ecosystem :**

| Système | Non système (désinstallables) |
|---|---|
| Terminal, File Explorer, Settings, Text Editor, System Monitor, Error Log Viewer, et toutes les commandes | Browser, Calculator, Code Editor, Diagnostic, HackPaint, Icon Viewer |

Les commandes sont toutes système : elles forment la base de l'OS, comme
coreutils sous Linux. L'ADR de phase 3 sur le regroupement des commandes dira si
des outils réseau (`curl`, `nmap`) sortent dans un projet non système à part.

Le statut système a deux effets : l'app n'est **pas désinstallable** (section 3)
et elle reçoit ses capacités dangereuses **d'office** (ADR 0049).

### 2. Désinstaller (D10)

Désinstaller une app :

- la cache du lanceur, de la recherche, du menu Démarrer et des listes « Ouvrir
  avec » ;
- bloque son lancement par toutes les voies (`AppLifecycleOrchestrator.LaunchAsync`,
  intentions, associations de fichiers, `launch`, résolution des commandes du
  terminal, qui répond `command not found`) ;
- arrête ses instances en cours, comme la désactivation de l'ADR 0032 ;
- retire ses associations de fichiers : `FileAssociationResolver` ignore une app
  désinstallée. Les préférences de `/etc/hackeros/file-associations.json` ne sont
  pas réécrites, donc une réinstallation les retrouve ;
- **ne supprime aucune donnée** : `~/.config/<appId>/`, `/etc/<appId>/`, les
  journaux et les fichiers de l'utilisateur restent en place.

**Dépendances :** désinstaller une app désinstalle aussi les apps installées qui
en dépendent obligatoirement (fermeture déjà calculée par l'ADR 0032). La boîte
de confirmation liste ces apps. Si une app système fait partie de cette fermeture,
la désinstallation est refusée. Réinstaller une app réinstalle aussi ses
dépendances obligatoires désinstallées dans la même portée.

### 3. Portée et fichiers d'état (D11)

L'état vit dans deux fichiers `.config` (grammaire de l'ADR 0011, conservée par
l'ADR 0044) :

| Portée | Fichier | Propriétaire et mode | Qui écrit |
|---|---|---|---|
| Machine | `/etc/hackeros/apps.config` | `system:system 0644` | un administrateur, à travers `sudo` ou l'invite d'approbation de l'ADR 0042 |
| Utilisateur | `~/.config/hackeros/apps.config` | l'utilisateur, `0644` | l'utilisateur lui-même |

Format :

```text
# /etc/hackeros/apps.config
[uninstalled]
org.hackeros.hack-paint=true
org.hackeros.calculator=true
```

- Les clés sont des identifiants d'app : le fichier est lu par
  `ConfigDocumentFormat.Parse` sans schéma (pas par `LayeredConfigResolver`), et
  seules les clés de la section `[uninstalled]` comptent.
- `true` : désinstallée. `false` ou absence : installée. Une autre valeur est
  ignorée avec un avertissement dans `/var/log/syslog`.
- Un identifiant inconnu de ce build est ignoré **sans** avertissement et sa
  ligne est conservée : le même fichier peut servir à plusieurs builds.
- Un identifiant d'app système est ignoré avec un avertissement : modifier le
  fichier avec `nano` ne permet pas de contourner la section 1.
- Un fichier absent ou illisible vaut « rien de désinstallé ».

**État effectif** pour l'utilisateur de la session : une app est désinstallée si
elle l'est dans le fichier machine **ou** dans son fichier utilisateur.

- Un utilisateur ne peut pas réinstaller pour lui une app désinstallée pour la
  machine ; l'app Paramètres affiche « Désinstallée par l'administrateur ».
- Un administrateur choisit la portée dans la boîte de confirmation : « Pour moi »
  ou « Pour tous les utilisateurs ». Un utilisateur standard n'a que « Pour moi ».

### 4. Service d'installation

`IAppInstallationService` / `AppInstallationService`
(`Platform/HackerOs.Platform.Core/Apps/`) :

```csharp
public interface IAppInstallationService
{
    AppInstallationState GetState(string appId);   // Installed, UninstalledForUser, UninstalledForMachine, System
    IReadOnlyList<string> GetUninstallClosure(string appId);
    ValueTask<AppInstallationResult> UninstallAsync(string appId, AppInstallationScope scope, CancellationToken ct = default);
    ValueTask<AppInstallationResult> ReinstallAsync(string appId, AppInstallationScope scope, CancellationToken ct = default);
    event EventHandler? Changed;
}

public enum AppInstallationScope { User, Machine }
```

- Il écrit les fichiers par l'API `Hack*` (ADR 0043) **avec le contexte de
  l'appelant**. Les droits du fichier font foi : une écriture machine par un
  utilisateur sans droit lève `HackUnauthorizedAccessException`, ce qui déclenche
  l'invite d'approbation administrateur de l'ADR 0042.
- Il relit les deux fichiers à la connexion et les surveille avec
  `HackFileSystemWatcher` : un `sudo nano /etc/hackeros/apps.config` prend effet
  sans redémarrage.
- Il alimente `AppEnablementRegistry` (`MarkDisabled` / `MarkEnabled`), qui reste
  le point de contrôle unique du lancement. À la déconnexion, la couche
  utilisateur est retirée.
- Chaque désinstallation et réinstallation est écrite dans `/var/log/syslog`
  (ADR 0045), par exemple
  `apps: org.hackeros.calculator uninstalled (scope=machine, by=alice)`.

Dans cette phase, l'app Paramètres utilise ce service directement. Son exposition
dans le SDK public, protégée par la capacité dangereuse `apps.manage` (ADR 0049),
arrive en phase 4 (plan, section 8.1).

### 5. Paramètres > Applications installées

L'onglet de l'ADR 0032 est refait :

- une ligne par app du catalogue : nom, éditeur, version, badge « Système » ;
- bouton **Désinstaller** pour une app non système installée, avec confirmation
  (fermeture des dépendances, choix de la portée pour un administrateur) ;
- bouton **Réinstaller** pour une app désinstallée dans une portée que
  l'utilisateur peut modifier ; sinon la mention « Désinstallée par
  l'administrateur » ;
- pas de bouton pour une app système ;
- l'interrupteur activer/désactiver disparaît.

Le lanceur (`AppLauncher`) filtre désormais les apps désinstallées, ce qui ferme
l'écart que l'ADR 0032 laissait ouvert.

### 6. Fin du drapeau `IsEnabled` et migration

- `IPersistentAppCatalogRepository` garde l'instantané des manifestes, mais
  `IsEnabled` n'est plus lu ni écrit par l'OS. `AppLifecycleOrchestrator.DisableAsync`
  et `EnableAsync` deviennent internes au service d'installation.
- **Migration au premier démarrage** (`AppEnablementToFilesMigration`, contexte
  système) : chaque enregistrement `IsEnabled = false` dont l'app n'est pas système
  est écrit dans `[uninstalled]` de `/etc/hackeros/apps.config`, puisque la
  désactivation de l'ADR 0032 valait pour toute la machine. Marqueur :
  `/var/lib/hackeros/migrations/app-enablement-to-files`. Le magasin IndexedDB et
  sa version de schéma ne changent pas
  (voir [`old/common-pitfalls.md`](../old/common-pitfalls.md)).
- **Synchronisation :** le domaine « AppCatalog » de l'ADR 0033
  (`AppCatalogSyncService`, `IAppCatalogSyncService`) est retiré. La couche
  utilisateur est sous `/home/<user>` et suit donc le domaine « FileSystem » de
  l'ADR 0030 ; `/etc` n'est pas synchronisé, comme pour l'ADR 0044.

### 7. Tests de contrat

- `isSystemApp` vient de la `ProjectReference` ; une app ne peut pas l'obtenir
  autrement.
- Désinstaller une app système est refusé par le service, et une ligne qui la
  désinstalle dans `apps.config` est ignorée avec un avertissement.
- Portée machine sans droit : `HackUnauthorizedAccessException` ; avec approbation
  administrateur : fichier écrit.
- État effectif = machine OU utilisateur ; un utilisateur ne réinstalle pas une
  désinstallation machine.
- Une app désinstallée n'apparaît ni dans le lanceur ni dans « Ouvrir avec », ne
  se lance par aucune voie et ses instances sont arrêtées ; ses données restent.
- Fermeture des dépendances à la désinstallation et à la réinstallation.
- Modification de `apps.config` par l'API fichier prise en compte sans
  redémarrage.
- Migration : un `IsEnabled = false` devient une ligne `[uninstalled]`, une seule
  fois.

## Conséquences

- `cat /etc/hackeros/apps.config` montre ce que l'administrateur a retiré, et
  l'édition du fichier fait le même effet que l'app Paramètres.
- Le terminal, l'explorateur et les Paramètres ne peuvent plus disparaître par
  erreur.
- Désinstaller ne détruit rien : une réinstallation retrouve les paramètres et
  les associations de l'app.
- Il n'y a plus d'état « désactivé » distinct de « désinstallé ». Une app non
  système qu'on veut garder installée mais bloquée n'a pas de réglage dédié ; ce
  besoin n'a pas été exprimé.
- La synchronisation d'enablement entre appareils de l'ADR 0033 disparaît ;
  seule la couche utilisateur voyage, avec le reste de `/home`.

## Chantiers d'implémentation

| Branche | Contenu | Dépend de |
|---|---|---|
| `rebuild/2-system-apps` | `IsSystemApp` lu depuis `BuiltInApplications.json`, `AppCatalog.IsSystemApp` et `SystemAppIds`, métadonnée posée sur les `ProjectReference` de l'Ecosystem selon la section 1, tests. | `2-builtin-applications` (ADR 0047) |
| `rebuild/2-app-uninstall` | `IAppInstallationService`, fichiers `apps.config`, surveillance, filtrage (lanceur, recherche, associations, intentions, terminal, `launch`), fermeture des dépendances, journal, migration depuis `IsEnabled`, retrait d'`AppCatalogSyncService`, nouvel onglet des Paramètres. | `2-system-apps` |

## Références

- ADR 0002 (autorité de confiance), ADR 0030 (synchronisation des fichiers),
  ADR 0032 (complété), ADR 0033 (remplacé en partie), ADR 0042 (invite
  d'approbation), ADR 0043 (API `Hack*`), ADR 0044 (paramètres en fichiers),
  ADR 0045 (journaux), ADR 0047 (`BuiltInApplications.json`), ADR 0049
  (capacités dangereuses).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 6.3 et 11.
