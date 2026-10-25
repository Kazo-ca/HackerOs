# Applications par défaut propres à chaque utilisateur

Ce document couvre la mise en œuvre des associations de fichiers par utilisateur. Les formats des deux fichiers, l'ordre de résolution, l'API `HackApps` et les capacités sont dans [`../framework/file-associations-api.md`](../framework/file-associations-api.md).

Chantier `v3/4b`, suite de `v3/4-intents`, selon
l'[ADR 0121](adr/0121-user-file-associations.md). Complète
[`app-intents.md`](../framework/app-intents.md) (résolution des intents) et
[`settings-files.md`](../framework/settings-files.md) (emplacements des fichiers).

## Objet

Chaque utilisateur choisit l'app qui ouvre un type de fichier (`.md`,
`text/plain`) ou un schéma d'URI (`https`) pour lui seul, sans droits
d'administrateur. Son choix passe avant celui de la machine, comme
`~/.config/mimeapps.list` passe avant `/etc/xdg/mimeapps.list` sous Linux.

## Architecture

```text
Paramètres / « Ouvrir avec » / xdg-mime
        │ HackApps.Get/Set/ResetDefaultHandlerAsync
        ▼
IAppCatalogGateway (AppCatalogGateway ; RemoteCatalogGateway dans un worker : cat.defaults, cat.setdef)
        │ capacités file-associations.read (+ filesystem.user-home.write pour écrire)
        ▼
FileAssociationResolver ──► UserFileAssociationStore (~/.config, sous l'identité de l'utilisateur)
        │                └─► FileAssociationSettingsDocuments (/etc, ou contenu intégré)
        ▼
AppIntentDispatcher (passe le principal à chaque résolution)
```

- **`UserFileAssociationStore`** (`Platform.Core/Intents`) : lit le fichier de
  l'utilisateur (validé par `FileAssociationSettingsValidator`), l'écrit en
  gardant les autres entrées et les autres actions d'une même cible. Crée
  `~/.config/hackeros` en `0755` et le fichier en `0644`.
- **`PrincipalTextFile`** : lecture et écriture d'un petit fichier texte du
  dossier personnel sous l'identité de l'utilisateur, partagées avec
  `IntentChoiceStore` (`intents.config`).
- **`FileAssociationIndex`** : rangé par cible **et** action, dans les deux
  couches.
- **`FileAssociationResolver`** : surcharges qui prennent l'utilisateur
  (`ResolveAsync(intent, user)`, `ResolveSchemeAsync(scheme, user)`,
  `GetHandlerAppIdsAsync(path, user)`), `GetConfiguredDefaultsAsync(user)` et
  `SetUserDefaultAsync(user, target, action, appId?)`. Les anciennes
  signatures lisent la couche système seulement.
- **`AppCatalogGateway`** reçoit le principal et le vérificateur de capacités
  de l'app.

## Interfaces

- **Paramètres > Associations de fichiers** (« Applications par défaut ») :
  une ligne par cible et action déclarées par les apps installées (extensions,
  puis types de média, puis schémas affichés `https:`), un sélecteur
  (« Défaut du système (X) », « Seule app (X) » ou « Demander à chaque fois »,
  puis les apps), le badge d'origine (à vous, défaut système, intégré) et un
  filtre. Composant `FileAssociationsPanel`.
- **Explorateur > Ouvrir avec** : case « Toujours utiliser cette app pour les
  fichiers .ext » quand l'app choisie déclare l'extension. L'explorateur
  déclare `file-associations.read`.
- **`xdg-mime`** ([`linux-commands.md`](linux-commands.md)) :
  `query default <type>`, `default <app> <type>...`, `query filetype <fichier>`.

## Décisions

- Même format JSON que le fichier de la machine plutôt que `mimeapps.list` :
  un seul validateur, un seul index.
- Pas de verrou de l'administrateur pour l'instant (question posée, ADR 0121 §8).
- Le fichier de la machine reste écrit à la main par un administrateur.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Intents/UserFileAssociationTests.cs` :
  ordre des couches, utilisateur par utilisateur, choix périmé, `open` et
  `edit` séparés, fichier `0644` et réinitialisation, fichier invalide gardé,
  refus (`InvalidTarget`, `InvalidAction`, `AppNotFound`, `AppCannotHandle`),
  schéma, « Ouvrir avec », liste des défauts.
- `AppIntentDispatcherTests.Open_uri_and_open_file_use_the_acting_users_own_default`.
- `AppCatalogGatewayTests` : capacités et ordre des lignes.
- `XdgMimeCommandTests`, parité `xdg-mime` dans `CommandParityTests`,
  `WireCodecIntentTests.Default_handlers_keep_target_action_app_and_layer`.
- `HackerOs.Apps.Settings.Tests/FileAssociationsPanelTests.cs` : lignes du
  panneau.

## Liste des tâches

- [x] ADR 0121.
- [x] Couche `~/.config/hackeros/file-associations.json` lue sous l'identité de l'utilisateur.
- [x] Index rangé par cible et action.
- [x] Ordre utilisateur, puis machine, dans toutes les résolutions.
- [x] API du SDK (`Unshipped`) et opérations des workers.
- [x] Panneau des Paramètres.
- [x] « Toujours utiliser » dans « Ouvrir avec ».
- [x] Commande `xdg-mime`.
- [x] Tests et documentation.
