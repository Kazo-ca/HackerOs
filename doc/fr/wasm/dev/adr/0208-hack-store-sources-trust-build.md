> [English version](../../../../en/wasm/dev/adr/0208-hack-store-sources-trust-build.md)

# ADR 0208 : sources de confiance, installation pour soi et validation en deux étapes, ajustements de mise en œuvre

## Statut

Accepté le 2026-10-30, chantier `todo/3-hackstore-sources-trust` (issue `TODO`
[#248](https://github.com/Kazo-ca/HackerOs/issues/248), Hack Store 3). Met en
œuvre l'[ADR 0189](0189-app-sources-trust-and-per-user-install.md). Ne change
aucune de ses décisions sauf une : le catalogue n'est pas construit « à
l'ouverture de la session » mais au démarrage de la page, avec un masquage par
session (§3 ci-dessous). Benoit a reçu cette question avec ses choix (Q1) ; la
recommandation est appliquée en attendant sa réponse.

## Contexte

L'ADR 0189 décide les sources de confiance, l'installation pour soi, la liste
noire et la fenêtre de validation en deux étapes. Il restait à fixer : la forme
des sources non fiables dans le fichier, comment l'hôte donne ses valeurs par
défaut, ce que choisit `install` sans option, comment un catalogue construit
une seule fois par page (ADR 0182 Q5 C) devient « propre à l'utilisateur », et
quelle authentification demandent les modifications des sources et de la liste
noire.

## Décision

1. **Fichier des sources.** `[trusted]` garde la forme `clé=true|false`. Une
   source ajoutée « non fiable » est écrite `false` : elle reste listée (et
   retirable) sans être de confiance. Clés acceptées : `catalog:<nom>`,
   `github.com/<owner>`, `github.com/<owner>/<repo>`. `[catalogs]` n'accepte que
   des adresses `https` sans identifiants. `[updates] interval-hours` va de 1 à
   720. Toute ligne invalide est ignorée avec un avertissement dans
   `/var/log/syslog`. Un dépôt GitHub n'est jamais « Vérifié » à lui seul : ce
   niveau est réservé aux apps du catalogue officiel installées par le Store
   (Hack Store 4, issue #249).
2. **Valeurs fixées à la compilation.** L'hôte (`HackerOsAppHost=true`) règle
   des propriétés MSBuild que `build/HackerOs.Apps.targets` transforme en
   `AssemblyMetadata` (préfixe `HackerOs.AppPackages.`) de son assembly ;
   `AppPackageDefaults.FromAssembly` les lit au démarrage. Une propriété vide
   garde la valeur standard (catalogue officiel, `catalog:official` et
   `github.com/Kazo-ca` de confiance, 6 h, rien de bloqué) :

   | Propriété | Exemple |
   |---|---|
   | `HackerOsAppCatalogs` | `official=https://…/catalog.json,school=https://…/catalog.json` |
   | `HackerOsTrustedAppSources` | `catalog:official,github.com/Kazo-ca` |
   | `HackerOsAppUpdateIntervalHours` | `6` |
   | `HackerOsBlockedApps`, `HackerOsBlockedAppCategories`, `HackerOsBlockedAppCapabilities` | `social` |

   Ces valeurs écrivent `app-sources.config` et `app-policy.config` quand ils
   n'existent pas, et restent « intégrées » (de confiance sans changement d'un
   administrateur) ; un administrateur peut quand même les mettre à `false`.
3. **Catalogue par session (révise le passage « à l'ouverture de la session »
   de l'ADR 0189 §3 et Conséquences).** Le catalogue est construit une seule
   fois par page, avant toute connexion. Au démarrage, `InstalledAppsImage`
   charge les apps du système puis les apps personnelles de tous les dossiers
   `/home/*`. Chaque session ne voit que les siennes : `InstalledPackageCatalog`
   sait à qui est chaque app, `AppInstallationService` les compte comme
   désinstallées pour les autres (lanceur, recherche, associations, terminal)
   et `IAppManagementGateway.GetAllAsync` ne les liste pas. Si deux
   utilisateurs ont des versions différentes de la même app, la plus récente
   est chargée pour les deux (avertissement dans le journal). Une copie
   personnelle d'une app déjà installée pour le système est ignorée.
4. **Choix de la portée.** `HackPackageScope.Auto` (sans option) installe pour
   soi quand c'est permis : session ouverte, source de confiance, app non
   bloquée, pas déjà installée pour le système. Sinon il passe à l'installation
   pour tous, qui ouvre les deux étapes ; une source non fiable déjà installée
   pour soi reste pour soi (mise à jour). `--user` refuse une app bloquée
   (`apps.package.blocked`) ou déjà installée pour le système
   (`apps.package.installed-for-system`). `hackpkg update` garde chaque copie
   là où elle est.
5. **Deux étapes.** `IAppInstallApprovalPrompt` est le contrat de l'étape 2 ;
   l'hôte le donne avec `AppInstallApprovalBroker` et
   `AppInstallApprovalOverlay`, rendu par le bureau, le mode mobile et chaque
   écran secondaire, comme la fenêtre d'authentification (ADR 0055 §5).
   L'étape 1 appelle l'agent d'authentification avec `IsRootElevated=false`,
   même dans un processus root : aucun `sudo` ne la remplace. Sans fenêtre
   (hôte sans interface), rien n'est installé pour tous.
6. **Sources et liste noire : une seule authentification.** Ajouter ou retirer
   une source ou une règle de la liste noire demande une authentification
   d'administrateur (rien sous `sudo`), comme le permet déjà `sudo nano` sur
   ces fichiers ; il n'y a pas d'app à montrer dans une fenêtre d'étape 2.
   Benoit a reçu la question (Q2) ; la recommandation est appliquée.
7. **SDK.** `HackAppPackages.InstallAsync(source, HackPackageScope)`,
   `RemoveAsync(appId, HackPackageScope)`, `AddSourceAsync`, `RemoveSourceAsync`,
   `GetPolicyAsync`, `SetPolicyAsync` ; `HackPackageSources.TrustedOwners`
   devient `TrustedSources` (pas encore livré) ; `HackPackageTrust`,
   `HackPackageCatalog`, `HackPackagePolicy`, `HackPackageBlockKind`. Les
   nouveaux membres de `IAppManagementGateway` ont une implémentation par
   défaut qui lève `NotSupportedException`.

## Conséquences

- Les fichiers d'une app personnelle d'un autre utilisateur sont dans le
  runtime de la page, même s'il ne la voit pas : la confiance dans la source
  et la liste noire restent la vraie barrière, comme le dit déjà l'ADR 0189.
- Une app installée pour soi apparaît après un redémarrage de HackerOS, comme
  toute app installée (ADR 0182 Q5 C).
- `--allow-untrusted` disparaît ; un script qui l'utilisait reçoit
  « invalid option » et le code 2.
- `hackpkg install <appId>` et `hackpkg info <appId>` (par le catalogue)
  arrivent avec le Store (issue #249).

## Tâches

- [x] `app-sources.config` : `[catalogs]`, `[updates]`, sources non fiables,
      valeurs fixées à la compilation (`AppPackageDefaults`, `HackerOs.Apps.targets`).
- [x] `app-policy.config` : lecture, défauts, création au démarrage (`FileSystemSeeder`).
- [x] Installation pour soi, une seule copie, catalogue par session (`InstalledPackageCatalog`).
- [x] Fenêtre de validation en deux étapes (`AppInstallApprovalBroker`, `AppInstallApprovalOverlay`).
- [x] `hackpkg` : `--user`, `--system`, `sources add/remove`, `policy`, retrait de `--allow-untrusted`.
- [x] Paramètres ▸ Apps ▸ Sources et liste noire (`AppSourcesPanel`).
- [x] Tests et documentation (`app-installation.md`, `linux-commands.md`).
