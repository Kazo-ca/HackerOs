# ADR 0096 : intents ajoutés (afficher, modifier, ouvrir une URI, réglages d'une app) et intents définis par les apps

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/4-intents`).

- **Complète :** ADR 0010 et 0047 (déclaration `Intents` du manifeste),
  [ADR 0039](0039-directory-open-file-association.md) (association des
  dossiers), ADR 0050 §7 (`ExecuteCommandIntent`), ADR 0056 (façades du SDK).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/4-intents` ; manque R6.

## Contexte

`AppIntentDispatcher` (`Platform/HackerOs.Platform.Core/Intents/AppIntentDispatcher.cs:160-169`)
traite `LaunchApp`, `OpenFile` et `ExecuteCommand`. Deux intents existent mais ne
font rien : `RevealFileIntent` (ligne 166) et `ShowAppSettingsIntent` (ligne 297,
qui valide la cible et répond « envoyé »). Il n'y a pas d'intent pour modifier un
fichier ni pour ouvrir une URI ; `HackBrowser.OpenAsync` lance directement le
navigateur de HackerOS. `IAppIntentGateway` n'expose que `LaunchAsync` et
`OpenFileAsync`. Une app peut déclarer des intents (`AppDefinition.Intents`,
champ `intents` du manifeste) mais le répartiteur répond toujours
`intent.unknown` à un identifiant qu'il ne connaît pas. Il n'existe pas de
commande `xdg-open`.

## Décision

### 1. Afficher un fichier (`RevealFileIntent`)

- Le répartiteur lance le gestionnaire par défaut des dossiers (type
  `inode/directory`, ADR 0039) sur le **dossier parent**, avec l'argument
  `--select=<chemin>`. C'est le contrat de `org.freedesktop.FileManager1.ShowItems`
  ramené à un argument de lancement.
- L'explorateur de fichiers ouvre le dossier et sélectionne l'élément, en le
  faisant défiler jusqu'à lui. Un autre gestionnaire de dossiers qui ignore
  `--select` ouvre simplement le dossier.
- Mêmes règles d'accès qu'`OpenFile` : l'appelant doit pouvoir lire le dossier
  parent, sinon `intent.reveal-file.denied` ; chemin absent :
  `intent.reveal-file.not-found`.

### 2. Modifier un fichier (`EditFileIntent`)

- Nouvel intent `org.hackeros.intent.edit-file.v1`, record
  `EditFileIntent(VirtualPath Path, string? MediaType)`.
- Il choisit parmi les gestionnaires dont `FileHandlerManifest.Actions` contient
  `edit`, avec les mêmes règles de priorité et de choix par défaut qu'`OpenFile`.
  Sans gestionnaire `edit`, il retombe sur `open` (comme `xdg-open`, qui n'a pas de
  verbe « modifier »), et le résultat le signale (`FellBackToOpen`).
- `IAppIntentGateway.EditFileAsync` et `HackApps.EditFileAsync` l'exposent.

### 3. Ouvrir une URI (`OpenUriIntent`) et `xdg-open`

- Nouvel intent `org.hackeros.intent.open-uri.v1`, record `OpenUriIntent(Uri Uri)`.
- Comme sous freedesktop, un schéma d'URI est un type de média
  `x-scheme-handler/<schéma>` : une app le déclare dans ses `FileHandlers`
  (`MediaType = "x-scheme-handler/https"`, action `open`) et le choix par défaut
  vit dans `/etc/hackeros/file-associations.json`, modifiable par
  l'administrateur et surchargeable par l'utilisateur, comme les autres
  associations. Le navigateur de HackerOS déclare `http` et `https` et en est le
  gestionnaire par défaut.
- `file://` devient un `OpenFileIntent` sur le chemin.
- Schéma sans gestionnaire : `intent.open-uri.no-handler`.
- `IAppIntentGateway.OpenUriAsync` et `HackApps.OpenUriAsync` l'exposent ;
  `HackBrowser.OpenAsync` reste, pour une app qui veut précisément le navigateur
  de HackerOS.
- Nouvelle commande `xdg-open <chemin|uri>` dans `HackerOs.Commands` : un
  chemin donne `OpenFile` (un dossier s'ouvre dans son gestionnaire, comme sous
  Linux), une URI donne `OpenUri`. Code de sortie 0 si
  l'intent est envoyé, 3 sans gestionnaire, 4 si le fichier n'existe pas (codes
  de `xdg-open`).

### 4. Réglages d'une app (`ShowAppSettingsIntent`)

- Si l'app cible déclare elle-même l'intent `org.hackeros.intent.show-app-settings.v1`
  dans `Intents`, c'est elle qui est lancée (ou activée) avec
  `--show-settings` : elle a sa propre page de réglages.
- Sinon, Paramètres s'ouvre sur la fiche de l'app (`--app=<id>`) : capacités
  accordées, associations, état du service et mode de démarrage pour une app de
  genre `Service`, désinstallation.

### 5. Intents définis par les apps

- Une app qui déclare un identifiant dans `Intents` **le reçoit** : le
  répartiteur ne répond plus `intent.unknown` à un identifiant déclaré par au
  moins une app installée.
- Nouvel intent générique `CustomIntent(string IntentId, string? PayloadJson)`,
  envoyé par `HackApps.SendIntentAsync(intentId, payload)`. Identifiant libre mais
  bien formé (`domaine.inversé.nom.vN`), comme une action Android.
- Routage : une seule app déclarante, elle est lancée ou activée ; plusieurs,
  l'utilisateur choisit dans le même sélecteur qu'« Ouvrir avec », avec la case
  « toujours utiliser » enregistrée dans `~/.config/hackeros/intents.config`.
- Réception : `IAppExecutionContext.Intent` (identifiant et charge utile) au
  lancement ; pour une app déjà ouverte et `SingleInstancePerUser`, un crochet
  `OnIntentReceived` de `WindowAppBase` et `TerminalApp`/`ServiceApp` équivalents.
- La charge utile est un objet JSON de 64 Kio au plus, vérifié bien formé.
  `PayloadSchemaAssetPath` reste une documentation pour les appelants : pas de
  validation de schéma au répartiteur (pas de bibliothèque JSON Schema dans
  l'OS).
- L'appelant doit avoir `apps.launch`, comme pour `LaunchApp` vers une autre app.

## Conséquences

- `AppIntentIds` gagne `edit-file`, `open-uri` et le préfixe libre ; le schéma
  du manifeste ne change pas (les déclarations existent déjà).
- `v3/4` ajoute les intents, `xdg-open`, `--select` dans l'explorateur, la fiche
  d'app dans Paramètres, et des tests du répartiteur pour chaque intent et chaque
  code d'erreur.
- `sdk-reference.md`, `app-model.md`, `file-explorer.md`, `linux-commands.md`
  et `shell-commands.md` sont mis à jour.
