> [English version](../../../../en/wasm/dev/adr/0121-user-file-associations.md)

# ADR 0121 : associations de fichiers et de schémas propres à chaque utilisateur

## Statut

Accepté le 2026-10-17 (chantier `v3/4b`, suite de `v3/4-intents`). Benoit a
choisi « Maintenant » sur la carte du fil `v3/4` (« Ajouter des associations de
fichiers et de schémas propres à chaque utilisateur ? »). Une question reste
posée (§8) ; ce qui suit applique la réponse recommandée en attendant.

- **Réalise :** la couche utilisateur annoncée par l'ADR 0096 §3 (« modifiable
  par l'administrateur et surchargeable par l'utilisateur, comme les autres
  associations ») et par l'ADR 0044 §8 (« une couche utilisateur pourra venir »).
- **Complète :** [ADR 0039](0039-directory-open-file-association.md) (dossiers),
  [ADR 0056](0056-sdk-apis-for-system-apps.md) §2 (`HackApps`, « Ouvrir avec »),
  [ADR 0096](0096-added-intents-and-app-defined-intents.md) (`xdg-open`).
- **Document :** [`app-intents.md`](../../framework/app-intents.md),
  [`settings-files.md`](../../framework/settings-files.md).

## Contexte

Le choix de l'app qui ouvre un type de fichier vit dans
`/etc/hackeros/file-associations.json`, modifiable par un administrateur
seulement. L'ADR 0096 suppose qu'un utilisateur peut surcharger ce choix, mais
aucune couche utilisateur n'existe : un utilisateur qui préfère l'éditeur de code
pour les `.md`, ou un autre navigateur pour `https`, n'a aucun moyen de le dire.
L'onglet « Associations de fichiers » des Paramètres ne fait que lister les types
déclarés par les apps. Sous Linux, freedesktop règle ce besoin par
`~/.config/mimeapps.list`, lu avant `/etc/xdg/mimeapps.list`, et par
`xdg-mime default`.

## Décision

### 1. Fichier de l'utilisateur

- `~/.config/hackeros/file-associations.json`, **même schéma** que le fichier
  système (`schemaVersion` 1, tableau `associations` d'entrées `extension` ou
  `mediaType`, `appId`, `actions`) et même validateur.
- Créé en `0644` dans `~/.config/hackeros` (`0755`) quand l'utilisateur fait son
  premier choix, comme `mimeapps.list`. Il est synchronisé avec le reste du
  dossier personnel (il n'est pas dans `~/.local/state`).
- Absent ou invalide, il ne compte pas : la résolution passe à la couche
  système. Une écriture ne remplace jamais un fichier invalide (on ne perd pas une
  faute de frappe faite avec `nano`).
- L'index des associations est désormais rangé par **cible et action** : deux
  entrées peuvent nommer des apps différentes pour `open` et `edit` d'un même
  type, dans les deux fichiers.

### 2. Ordre de résolution

1. L'app demandée explicitement par l'intent.
2. Le défaut de **l'utilisateur qui exécute l'intent** (le principal, pas
   forcément l'utilisateur de la session : `sudo -u bob xdg-open` lit le fichier
   de `bob`).
3. Le défaut du système.
4. La seule app candidate, sinon le sélecteur.

Une entrée qui nomme une app inconnue ou désinstallée est ignorée, couche par
couche : un choix périmé de l'utilisateur retombe sur celui de l'administrateur.
L'ordre vaut pour tout ce qui résout une association : `OpenFile`, `EditFile`,
`OpenUri` (`x-scheme-handler/<schéma>`), `RevealFile` (`inode/directory`) et la
liste « Ouvrir avec » (le défaut en tête).

### 3. API du SDK (mineure, `Unshipped`)

- `IAppCatalogGateway.GetDefaultHandlersAsync` et
  `SetUserDefaultHandlerAsync(target, action, appId?)`, membres par défaut (une
  passerelle plus ancienne lève `NotSupportedException`).
- `HackApps.GetDefaultHandlersAsync()`, `SetDefaultHandlerAsync(target, appId,
  action = "open")`, `ResetDefaultHandlerAsync(target, action = "open")`.
- `HackFileAssociation(Target, Action, AppId, Layer)` : les lignes de
  l'utilisateur d'abord, puis celles du système ; la première ligne d'une cible
  et d'une action est celle que l'OS utilise.
- `HackDefaultHandlerStatus` : `Changed`, `InvalidTarget`, `InvalidAction`,
  `AppNotFound`, `AppCannotHandle`, `FileInvalid`, `WriteFailed`.
- Une cible est une extension avec son point (`.md`) ou un type de média
  `type/sous-type` (`text/markdown`, `x-scheme-handler/https`), mise en
  minuscules. L'app doit être une app fenêtre installée qui **déclare** la cible
  et l'action dans son manifeste ; retirer un choix est toujours permis.
- **Capacités :** lire demande `file-associations.read` ; changer son propre
  choix demande en plus `filesystem.user-home.write`, puisque c'est un fichier du
  dossier personnel que l'app pourrait écrire de toute façon.
  `file-associations.write` (dangereuse) reste celle du fichier de la machine.
- Les apps des workers passent par deux opérations, `cat.defaults` et
  `cat.setdef`.

### 4. Paramètres et « Ouvrir avec »

- L'onglet « Associations de fichiers » devient « Applications par défaut » :
  une ligne par cible et action déclarées par les apps installées, avec un
  sélecteur (« Défaut du système (X) », « Seule app (X) » ou « Demander à chaque
  fois », puis les apps candidates), le badge d'origine (à vous, défaut système,
  intégré) et un filtre. Le fichier de la machine est montré, jamais écrit ici.
- La boîte « Ouvrir avec » de l'explorateur propose « Toujours utiliser cette
  app pour les fichiers .ext » quand l'app choisie déclare l'extension. L'explorateur
  reçoit `file-associations.read` pour cela.

### 5. Commande `xdg-mime`

- `xdg-mime query default <type>` : l'app que l'OS utilise pour ouvrir ce type
  pour cet utilisateur (rien si aucun défaut).
- `xdg-mime default <app> <type>...` : écrit le choix de l'utilisateur ; un
  `.desktop` final est ignoré, comme freedesktop nomme ses apps.
- `xdg-mime query filetype <fichier>` : `inode/directory` pour un dossier, sinon
  le type de média qu'une app installée déclare pour l'extension, sinon
  l'extension elle-même, sinon `application/octet-stream`. HackerOS n'a pas de
  base `shared-mime-info`.
- Codes de sortie de `xdg-mime` : 0, 1 ligne de commande, 2 fichier absent, 4
  action refusée. Seule l'action `open` est réglable ici, comme sous freedesktop ;
  `edit` se règle dans les Paramètres ou par le SDK.

### 6. Ce qui ne change pas

- Les intents définis par les apps gardent `~/.config/hackeros/intents.config`
  (ADR 0096 §5).
- La synchronisation `FileAssociations` (ADR 0033) reste celle du fichier de la
  machine.

### 7. Options écartées

- **Format `mimeapps.list` (INI)** : plus fidèle à Linux, mais deux formats pour
  un même contenu ; le même JSON garde un seul validateur et un seul index.
- **Nouvelle capacité « associations de l'utilisateur »** : elle ne protège rien
  que `filesystem.user-home.write` ne permette déjà, et ajoute une ligne de plus
  à accorder.
- **Écrire le fichier de la machine depuis les Paramètres** : resterait un geste
  d'administrateur avec approbation ; hors du besoin exprimé.

### 8. Question posée à Benoit : un administrateur peut-il verrouiller un défaut ?

Les autres réglages ont une section `[locked]` (ADR 0044) qui empêche
l'utilisateur de surcharger une clé. Rien de tel n'existe ici : un utilisateur
peut toujours choisir son app.

- **Pas de verrou (recommandé, appliqué)** : comme freedesktop, le choix d'un
  utilisateur ne touche que lui.
- **Verrou par entrée** (`"locked": true` dans le fichier de la machine) : la
  couche utilisateur est ignorée pour cette entrée et les Paramètres grisent la
  ligne ; demande un nouvel ADR et un champ de schéma.

## Conséquences

- 70 commandes (`xdg-mime` en plus, après `passwd`), 87 apps intégrées.
- Un fichier `~/.config/hackeros/file-associations.json` apparaît au premier
  choix ; l'effacer rend la main au défaut du système.
- `FileAssociationResolver` lit le système de fichiers sous l'identité de
  l'utilisateur ; ses anciennes signatures restent (couche système seulement).
