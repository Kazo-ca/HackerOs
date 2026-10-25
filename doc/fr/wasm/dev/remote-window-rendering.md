> [English version](../../../en/wasm/dev/remote-window-rendering.md)

# Rendu d'une fenêtre dans un Web Worker (essai)

> **Statut : essai terminé, verdict « on s'arrête »** ([ADR 0133](adr/0133-window-rendering-in-worker-verdict.md)).
> Le code reste dans le dépôt, coupé par défaut. Aucune app ne doit en
> dépendre ; les fenêtres tournent sur le fil principal (ADR 0070 §1) et une
> charge lourde passe par `HackBackground` ([`background-jobs.md`](../framework/background-jobs.md)).

## Rôle

Montrer qu'une app à fenêtre Blazor peut s'exécuter dans un Web Worker, sans
DOM, tout en se dessinant dans sa fenêtre sur la page, et mesurer ce que cela
coûte (ADR 0129). Le sujet est la Calculatrice, inchangée.

## Activer l'essai

Dans `OS/HackerOs.Ecosystem/wwwroot/appsettings.json` :

```json
"HackerOs": {
  "RemoteWindowRendering": {
    "Enabled": true,
    "Apps": [ "org.hackeros.calculator" ]
  }
}
```

- `Enabled` (défaut `false`) : sans lui, aucun service n'est enregistré et
  rien ne change.
- `Apps` : identifiants des apps à fenêtre rendues dans un worker. Une app
  avec de l'interop JavaScript propre ne marchera pas (pas d'`IJSRuntime`
  dans le worker).
- Seulement dans le navigateur, avec les workers (`AddHackerOsWorkerProcesses`).
  Si l'hôte des processus distants n'est pas celui des workers (tests, Server
  en mode interactif), la fenêtre se rend sur la page comme avant.

## Architecture

```
Page (fil principal)                               Worker dédié
────────────────────                               ────────────
WindowAppRenderer                                  WorkerProcessHost
  └─ IRemoteWindowRendering ? ── oui ──┐             └─ start k=3 → WorkerWindowRunner
       RemoteWindowHost (razor)        │                  ├─ WorkerWindowRenderer : Renderer
         ├─ StartWindowAsync ──────────┼── start ──►      │    └─ WorkerWindowRoot
         │                             │                  │         └─ CascadingValue(AppContext)
         │                             │                  │              └─ CalculatorWindow
         ├─ RemoteWindowHost.razor.js ◄┼── ui {k:"b"} ─── │  DomPatchWriter (RenderBatch → ops)
         │    (exécute les ops)        │                  │
         └─ DispatchEvent ─────────────┼── uin {k:"e"} ─► │  DomEventArgsReader → DispatchEventAsync
              dialogues de l'OS ◄──────┼── ui {k:"dlg"}   └─ RemoteDialogService
                                 ──────┼── uin {k:"dlgr"} ►
```

| Projet | Types |
|---|---|
| `Platform/HackerOs.Platform.Blazor` | `IRemoteWindowRendering` (`GetHostComponent`) ; `WindowAppRenderer` le consulte s'il est enregistré. |
| `Platform/HackerOs.Platform.WorkerHost` | Messages `ui` (worker → OS) et `uin` (OS → worker) ; `start` avec `k = 3`, `ra`/`rt` (le runner) ; côté worker `IWorkerWindowRunner`, `WorkerWindowRun` ; côté OS `WorkerRemoteProcessHost.StartWindowAsync`, `RemoteWindowProcess`, `RemoteProcessSession.UiReceiver`. |
| `Platform/HackerOs.Platform.RemoteRendering` (nouveau) | **Worker/** : `WorkerWindowRunner`, `WorkerWindowRenderer`, `WorkerWindowRoot`, `DomPatchWriter`, `DomEventArgsReader`, `RemoteDialogService`. **Host/** : `RemoteWindowHost` (+ `.razor.cs`, `.razor.css`, `.razor.js`), `RemoteWindowRendering`, `RemoteWindowRenderingOptions`. `AddHackerOsRemoteWindowRendering`. |
| `OS/HackerOs.Ecosystem.Host` | Lit la section `HackerOs:RemoteWindowRendering` et appelle `AddHackerOsRemoteWindowRendering`. |

### Processus

- La fenêtre prend le worker prêt comme un processus `dedicated` (ADR 0071) ;
  elle a son pid, son contexte distant et ses passerelles (ADR 0070, 0124).
- Le chrome (titre, bordures, boutons, raccourcis) reste dessiné par la page :
  une fenêtre qui gèle dans son worker se déplace et se ferme toujours.
- Fermer la fenêtre envoie `{k:"close"}` ; après 2 s sans fin, le worker est
  terminé. Une fin anormale affiche une alerte dans la fenêtre.

### Opérations DOM

Le worker garde un miroir de l'arbre logique de Blazor (les composants et les
régions n'ont pas de nœud DOM) et écrit, pour chaque `RenderBatch`, un message
`ui` `{k:"b", n, o:[...], us, nodes}`. Le nœud 0 est l'élément de contenu de la
fenêtre ; `before = 0` veut dire « à la fin ».

| Code | Forme | Rôle |
|---|---|---|
| 1 | `[1, id, parent, before, tag, svg]` | Créer un élément (SVG si `svg = 1`). |
| 2 | `[2, id, parent, before, texte]` | Créer un nœud texte. |
| 3 | `[3, id, parent, before, html]` | Insérer du markup (un ou plusieurs nœuds). |
| 4 | `[4, id]` | Retirer un nœud et son sous-arbre. |
| 5 | `[5, id, nom, valeur]` | Poser un attribut ; `value` et `checked` sont posés comme propriétés. |
| 6 | `[6, id, nom]` | Retirer un attribut. |
| 7 | `[7, id, texte]` | Changer un texte. |
| 8 | `[8, id, événement, gestionnaire]` | Écouter un événement (`0` = arrêter). |
| 9 | `[9, id, parent, before]` | Déplacer (listes à `@key`). |
| 10 | `[10, id, 'p'\|'s', événement, 0\|1]` | `preventDefault` / `stopPropagation`. |

Un attribut booléen vrai devient un attribut vide ; faux ou `null` le retire.

### Événements

La page appelle `DispatchEvent(gestionnaire, nom, json)` sur l'hôte, qui
envoie `uin` `{k:"e", h, n, a}`. Le worker reconstruit `MouseEventArgs`,
`PointerEventArgs`, `WheelEventArgs`, `KeyboardEventArgs`, `ChangeEventArgs`
ou `FocusEventArgs`, et pour `input`/`change` un `EventFieldInfo` (le champ
garde ce que l'utilisateur tape). Un événement pour un gestionnaire déjà
retiré est noté au journal et ignoré.

### Dialogues du SDK

| Dialogue | Dans un worker |
|---|---|
| `MessageBoxAsync`, `TextInputAsync` | Relayés : `{k:"dlg"}` → dialogue de l'OS sur la page → `{k:"dlgr"}`. |
| `CredentialPromptAsync` | Répond « annulé » : aucun mot de passe n'entre dans un worker. |
| Dialogues de fichiers | `NotSupportedException`. |

## Mesures

Voir l'[ADR 0133](adr/0133-window-rendering-in-worker-verdict.md) : ouverture
+≈ 340 ms, latence p95 +3,3 ms, **66,5 Mio** par worker de fenêtre (55,4 Mio
pour le runtime et le jeu d'assemblies du boot), lots de 72 octets en moyenne.

Pour les refaire :

```bash
build/publish-trimmed.sh   # ou dotnet publish OS/HackerOs.Ecosystem -c Release -o artifacts/publish-trimmed/publish
python3 -m http.server 8601 --bind 127.0.0.1 -d artifacts/publish-trimmed/publish/wwwroot &
HACKEROS_TRIMMED_URL=http://127.0.0.1:8601 HACKEROS_TRIMMED_ARTIFACTS=$PWD/artifacts/publish-trimmed/remote \
  dotnet test Tests/HackerOs.TrimmedPublish.E2E.Tests -c Release --filter "FullyQualifiedName~calculator"
```

Le test active l'essai en réécrivant `appsettings.json` à la volée et écrit
`remote-window-measurements.json`.

## Décisions clés

- **Traduire dans le worker** plutôt que rejouer le `RenderBatch` côté page :
  le format binaire de Blazor et son applicateur sont internes (ADR 0129 §2).
- **`BL0006` désactivé** dans les fichiers qui lisent les types de
  `RenderTree` : publics mais annoncés comme changeants ; les tests unitaires
  sont le garde-fou d'une montée de .NET.
- **`RemoteWindowHost` est public** : Razor génère des composants publics ; il
  est dans `PublicAPI.Unshipped.txt` de la plateforme, pas du SDK.
- **Le runner n'est pas vérifié par `KnownAssemblies`** : il est nommé par
  l'OS, pas par l'app ; seule l'assembly de l'app l'est.

## Limites

- Pas de focus par programme, pas d'interop JavaScript de l'app, pas de
  fenêtre détachée (ADR 0078), pas de dialogues de fichiers.
- Un worker par fenêtre : ≈ 66 Mio chacune.

## Tests

| Projet | Ce qu'il vérifie |
|---|---|
| `Tests/HackerOs.Platform.RemoteRendering.Tests` | `WorkerWindowRenderer` + `DomPatchWriter` rendent de vrais composants dans un faux DOM (`FakeDom`, même logique que le module JavaScript) : premier rendu, textes, éléments conditionnels, attributs booléens, composants et régions aplatis, liste à `@key` déplacée sans recréation, `@bind` (texte, case), drapeaux d'événements, SVG, gestionnaire retiré, gestionnaire qui lève ; `DomEventArgsReader` ; `RemoteDialogService`. |
| `Tests/HackerOs.TrimmedPublish.E2E.Tests` (`TrimmedPublishTests.RemoteWindows.cs`) | Sur la publication trimmée : la Calculatrice en worker calcule à la souris et au clavier, garde son CSS, `Tab`, axe, `Alt+F7`/`Alt+Q`, worker dédié libéré ; et les mesures. |

## Liste des tâches du chantier `exec/6-remote-renderer-poc`

- [x] Messages `ui`/`uin` et démarrage d'une fenêtre (`start` `k = 3`) dans `WorkerHost`.
- [x] `Renderer` du worker, racine en cascade, traducteur d'opérations DOM.
- [x] Hôte de fenêtre de la page et exécuteur JavaScript collocalisé.
- [x] Événements (souris, pointeur, molette, clavier, saisie, focus) et `@bind`.
- [x] Dialogues du SDK relayés (message, saisie) ; identifiants et fichiers refusés.
- [x] Réglage `HackerOs:RemoteWindowRendering`, coupé par défaut.
- [x] Test E2E sur la publication trimmée et mesures automatisées.
- [x] Tests unitaires du traducteur, des événements et des dialogues.
- [x] ADR 0133 : verdict « on s'arrête » ; carte de décision à Benoit.
- [x] Docs : ce fichier, `worker-processes.md`, index.
