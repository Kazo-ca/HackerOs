> [English version](../../../en/wasm/framework/app-intents.md)

# Intents des apps : afficher, modifier, ouvrir une URI, réglages et intents définis par les apps

Chantier `v3/4-intents` de la migration wasm v3, selon
l'[ADR 0096](../dev/adr/0096-added-intents-and-app-defined-intents.md) (qui complète
les ADR 0010, 0039, 0047, 0050 §7 et 0056).

## Objet

Une app demande à l'OS « ouvre ceci » sans savoir quelle app le fera : c'est un
intent. Avant ce chantier, le répartiteur ne traitait que `LaunchApp`,
`OpenFile` et `ExecuteCommand` ; « afficher dans le dossier » et « réglages de
l'app » répondaient « envoyé » sans rien faire, et un identifiant déclaré par
une app donnait toujours `intent.unknown`. Le chantier ajoute :

| Intent | Identifiant | Ce que fait l'OS |
|---|---|---|
| `RevealFileIntent(Path)` | `org.hackeros.intent.reveal-file.v1` | Lance le gestionnaire des dossiers (`inode/directory`) sur le dossier parent avec `--select=<chemin>` ; l'explorateur sélectionne l'élément et le fait défiler. C'est `org.freedesktop.FileManager1.ShowItems` ramené à un argument. |
| `EditFileIntent(Path, MediaType, PreferredAppId)` | `org.hackeros.intent.edit-file.v1` | Choisit parmi les apps dont le gestionnaire déclare l'action `edit` ; sans aucune, retombe sur `open` (comme `xdg-open`, qui n'a pas de verbe « modifier ») et le signale (`FellBackToOpen`). |
| `OpenUriIntent(Uri, PreferredAppId)` | `org.hackeros.intent.open-uri.v1` | `file://` devient un `OpenFileIntent` ; un autre schéma se résout comme le type de média `x-scheme-handler/<schéma>`, et l'app reçoit l'URI en argument. |
| `ShowAppSettingsIntent(TargetAppId)` | `org.hackeros.intent.show-app-settings.v1` | L'app cible avec `--show-settings` si elle déclare cet intent (elle a sa propre page), sinon Paramètres sur la fiche de l'app (`--app=<id>`). |
| `CustomIntent(IntentId, PayloadJson, TargetAppId, RememberChoice)` | identifiant libre `domaine.inversé.nom.vN` | Lance ou active l'app qui déclare l'identifiant dans `Intents` ; plusieurs : l'utilisateur choisit dans le sélecteur « Open with », avec « Always use this app » retenu dans `~/.config/hackeros/intents.config`. |

Et la commande `xdg-open <fichier|dossier|URI>`.

## Architecture

```text
App ──HackApps.*Async──► IAppIntentGateway ──► AppIntentDispatcher ──► FileAssociationResolver
 │                       (AppIntentGateway,      (capacités, accès,       (/etc/hackeros/
 │                        ou RemoteIntentGateway  erreurs intent.*)        file-associations.json,
 │                        dans un Web Worker)            │                 FileHandlers des manifestes ;
 │                                                       │                 ~/.config/hackeros d'abord,
 │                                                       │                 ADR 0121)
 │                                                       ▼
 └◄── AppContext.Intent / IntentReceived ◄── AppLifecycleOrchestrator.LaunchAsync(AppLaunchRequest.Intent)
```

- **`AppIntentDispatcher`** (`Platform.Core/Intents`) : une méthode par intent
  (`DispatchRevealAsync`, `DispatchEditFileAsync`, `DispatchOpenUriAsync`,
  `DispatchShowSettingsAsync`, `DispatchCustomAsync`). Nouveaux statuts
  `AccessDenied` et `Invalid` ; `FellBackToOpen` sur le résultat.
- **`FileAssociationResolver.ResolveSchemeAsync(scheme, preferredAppId)`** :
  résout `x-scheme-handler/<schéma>` avec l'action `open`. Le document par
  défaut nomme le navigateur pour `http` et `https`, à côté de
  l'explorateur pour `inode/directory`.
- **`IntentChoiceStore`** : lit et écrit `intents.config` (lignes
  `intentId=appId`, commentaires gardés), comme `mimeapps.list`. Le dossier est
  créé en `0755` et le fichier en `0600`, sous l'identité de l'utilisateur.
- **`PrincipalFileAccess.ForUser`** : contexte de fichiers de l'utilisateur
  (lecture du dossier personnel et du système, ses groupes), pour vérifier
  qu'il peut lire le dossier à afficher, comme le gestionnaire de fichiers le
  ferait en son nom.
- **Remise de l'intent** : `AppLaunchRequest.Intent` (`AppIntentDelivery` :
  `IntentId`, `PayloadJson`, `SenderAppId`, `Arguments`) devient
  `IAppExecutionContext.Intent` au lancement. Pour une instance déjà ouverte
  (fenêtre `SingleInstancePerUser`, service, site), l'orchestrateur lève
  `IntentReceived` sur son contexte, comme une `GApplication` qui reçoit la
  ligne de commande d'un second lancement ; un lancement avec arguments mais
  sans intent arrive comme une activation `LaunchApp`.
- **Web Workers** : opérations `int.edit`, `int.reveal`, `int.uri`,
  `int.settings`, `int.send` ; l'intent de départ voyage dans le message de
  démarrage (`it`) et un intent remis plus tard dans un message `intent`
  (OS → worker). `xdg-open` tourne dans le pool et se comporte comme sur le fil
  principal (test de parité).

## Usage et API

### Envoyer

```csharp
await HackApps.RevealFileAsync("~/Documents/rapport.txt");      // « Afficher dans le dossier »
HackOpenFileResult edit = await HackApps.EditFileAsync("notes.md");
HackIntentResult web = await HackApps.OpenUriAsync("https://hackersearch.hack/?q=nmap");
await HackApps.ShowSettingsAsync();                              // réglages de l'app courante
HackIntentResult sent = await HackApps.SendIntentAsync(
    "com.example.notes.share.v1", """{"text":"Bonjour"}""");
```

| Membre | Capacité | Échecs |
|---|---|---|
| `RevealFileAsync(path)` | `apps.launch` | `HackFileNotFoundException`, `HackUnauthorizedAccessException` (dossier illisible), `HackAppLaunchException` (aucun gestionnaire de dossiers) |
| `EditFileAsync(path)` | `apps.launch` | `HackOpenFileResult` : `Opened`, `ChooserRequired` (candidats), `NoHandler` ; `FellBackToOpen` |
| `OpenUriAsync(uri, appId)` | `apps.launch` | `ArgumentException` si l'URI n'est pas absolue ; `HackIntentResult` : `Delivered`, `ChooserRequired`, `NoHandler` |
| `ShowSettingsAsync(appId)` | aucune | `HackAppLaunchException` (app inconnue ou désactivée) |
| `SendIntentAsync(id, payload, targetAppId, rememberChoice)` | `apps.launch` | `ArgumentException` (identifiant mal formé ou réservé, charge utile qui n'est pas un objet JSON ou dépasse 64 Kio, cible qui ne déclare pas l'intent) ; `HackIntentResult` |

Une app fenêtrée appelle plutôt `WindowAppBase.SendIntentAsync(id, payload)` :
si plusieurs apps répondent, elle affiche le sélecteur de l'OS
(`ChooseAppAsync`, `AppChooserDialog`) puis renvoie l'intent vers l'app
choisie, avec « Always use this app » si l'utilisateur l'a coché. Le sélecteur
est affiché par l'appelant, comme pour `OpenFile` : le répartiteur ne fait
jamais apparaître de fenêtre dans une autre app.

### Recevoir

```csharp
public override IReadOnlyList<AppIntentDeclarationManifest> Intents =>
    [new("com.example.notes.share.v1", PayloadSchemaAssetPath: "schemas/share.json")];
```

- Au lancement : `LaunchIntent` (fenêtre) ou `AppContext.Intent`.
- Déjà ouverte : `protected override Task OnIntentReceivedAsync(AppIntentDelivery intent)`
  (`WindowAppBase`) ou `OnIntentReceivedAsync(intent, token)` (`ServiceAppBase`).
  Une commande, qui ne vit que le temps d'une ligne, lit seulement
  `AppContext.Intent`.
- `PayloadSchemaAssetPath` documente le contrat pour les appelants ; l'OS ne
  valide pas le schéma (pas de bibliothèque JSON Schema dans l'OS), seulement
  la forme : un objet JSON de 64 Kio au plus.

### Déclarer un schéma d'URI

```csharp
public override IReadOnlyList<FileHandler> FileHandlers =>
    [new("x-scheme-handler/gopher", [], ["open"])];
```

### Codes d'erreur du répartiteur

| Intent | Codes |
|---|---|
| Tous sauf `ShowAppSettings` | `intent.capability-denied` (sans `apps.launch`) |
| `RevealFile` | `intent.reveal-file.not-found`, `.denied`, `.chooser-required`, `.no-handler` |
| `EditFile` | `intent.edit-file.chooser-required`, `.no-handler`, `.target-invalid` |
| `OpenUri` | `intent.open-uri.invalid`, `.chooser-required`, `.no-handler`, `.target-invalid` ; `file://` donne les codes `intent.open-file.*` |
| `ShowAppSettings` | `intent.show-settings.not-found`, `.disabled`, `.no-settings-app` |
| Intent d'app | `intent.custom.invalid-id`, `.reserved-id`, `.payload-too-large`, `.payload-invalid`, `.target-invalid`, `.chooser-required`, `intent.unknown` (personne ne le déclare) |

`IAppIntentGateway` traduit ces statuts en `AppIntentOutcome` : un code
`.no-handler` ou `intent.unknown` devient `NoHandler`, `TargetInvalid` devient
`Invalid`, et un refus de capacité lève `AppGatewayAccessDeniedException`
comme `LaunchAsync`.

### `xdg-open`

Voir [`linux-commands.md`](../dev/linux-commands.md#fichiers) : chemin → `OpenFile`
(un dossier en `inode/directory`), URI → `OpenUri`. Codes 0 envoyé, 1 ligne de
commande, 3 aucune app, 4 fichier absent ou échec ; plusieurs apps sans défaut
donnent 4 et la liste des candidates.

### Explorateur et Paramètres

- Explorateur : `<dossier>` et `--select=<chemin>`, détails dans
  [`file-explorer.md`](../dev/file-explorer.md#ligne-de-commande-et--afficher-dans-le-dossier--adr-0039-adr-0096-1).
- Paramètres : `--app=<id>` ouvre l'onglet « Application » (`AppDetailsPanel`) :
  identité, capacités demandées avec l'état des capacités dangereuses
  (`HackPermissionAdministration`), types de fichiers et schémas gérés, état
  du service, mode de démarrage et boutons Start/Stop pour une app `Service`
  (`HackServiceController`, d'où la capacité `services.manage` de Paramètres),
  et la désinstallation (le panneau « Installed Apps » filtré sur l'app).

## Décisions clés

- **`RevealFile` demande `apps.launch`** (« mêmes règles qu'`OpenFile` », ADR
  0096 §1) et vérifie le chemin sous l'identité de l'utilisateur : un fichier
  absent ou un dossier illisible ne lance rien. Avant, il ne demandait aucune
  capacité et ne vérifiait rien.
- **`ShowAppSettings` ne demande aucune capacité** : il ouvre une surface du
  système et ne transporte aucune donnée de l'appelant, comme les panneaux de
  réglages de freedesktop.
- **Un identifiant intégré ne peut pas être envoyé comme intent d'app**
  (`intent.custom.reserved-id`) : il a un record typé avec ses propres règles,
  qu'un envoi non typé contournerait.
- **Le choix par défaut d'un schéma vit dans `file-associations.json`**, comme
  celui d'un type de fichier : celui de l'utilisateur
  (`~/.config/hackeros/file-associations.json`) passe avant celui de la machine
  (`/etc/hackeros/file-associations.json`) depuis le chantier `v3/4b`
  ([ADR 0121](../dev/adr/0121-user-file-associations.md),
  [`user-file-associations.md`](../dev/user-file-associations.md)).
- **Un terminal n'a pas de sélecteur** : `xdg-open` avec plusieurs apps et
  aucun défaut sort avec 4 et liste les candidates ; l'OS ne devine jamais.
- **L'explorateur honore enfin son argument de dossier** : ouvrir un dossier
  depuis une autre app ouvrait toujours le dossier personnel.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Intents/AppIntentDispatcherTests.cs` :
  chaque intent et chaque code d'erreur, arguments et `Intent` reçus par l'app
  lancée, choix retenu dans `intents.config`, remise à une instance ouverte
  par `IntentReceived`, traduction par `AppIntentGateway`.
- `Tests/HackerOs.Commands.Tests/XdgOpenCommandTests.cs` et le scénario
  `xdg-open` de `CommandParityTests` (worker et fil principal).
- `Tests/HackerOs.Apps.FileExplorer.Tests/FileExplorerLaunchArgumentsTests.cs`,
  `Tests/HackerOs.Apps.Settings.Tests/SettingsWindowTests.cs` (`--app=`).
- `Tests/HackerOs.Platform.Blazor.Tests/DialogCoordinatorTests.cs` (sélecteur
  d'apps), `Tests/HackerOs.AppSdk.Tests/Apps/HackAppsIntentTests.cs` (façades),
  `Tests/HackerOs.Platform.WorkerHost.Tests/WireCodecIntentTests.cs` (codec).

## Tâches

- [x] `AppIntentIds` : `edit-file`, `open-uri`, préfixe `x-scheme-handler/`, `IsWellFormed`, `IsBuiltIn`, 64 Kio.
- [x] Records `EditFileIntent`, `OpenUriIntent`, `CustomIntent`, `AppIntentDelivery`.
- [x] Répartiteur : `RevealFile` réel, `EditFile` avec repli sur `open`, `OpenUri`, `ShowAppSettings` réel, intents d'app.
- [x] `ResolveSchemeAsync` et navigateur par défaut pour `http`/`https`.
- [x] `intents.config` (« Always use this app »).
- [x] `IAppIntentGateway` : `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowAppSettingsAsync`, `SendIntentAsync`.
- [x] `IAppExecutionContext.Intent` et `IntentReceived` ; seconde activation d'une instance ouverte.
- [x] `HackApps` ; `WindowAppBase` et `ServiceAppBase` (`OnIntentReceivedAsync`, `SendIntentAsync`, `ChooseAppAsync`).
- [x] Sélecteur d'apps de l'OS (`AppChooserDialog`).
- [x] Web Workers : opérations, message `intent`, codec.
- [x] Commande `xdg-open`.
- [x] Explorateur : dossier de départ et `--select` (`FileView.RevealAsync`).
- [x] Paramètres : fiche d'une app (`--app=<id>`).
- [x] Navigateur : `x-scheme-handler/http` et `https`.
- [x] Tests, ce document, `sdk-reference.md`, `app-model.md`, `file-explorer.md`, `linux-commands.md`, `shell-commands.md`, `worker-processes.md`, plan et index.
