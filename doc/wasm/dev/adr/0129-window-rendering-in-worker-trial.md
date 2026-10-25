# ADR 0129 : essai du rendu d'une fenêtre Blazor dans un worker, et critères pour y aller

## Statut

Accepté le 2026-10-18 **comme essai**. Rien n'est livré aux apps par cet ADR.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décision 7, étape 2 (réponse de Benoit : « garder l'essai ») ; mené par
  `exec/6-remote-renderer-poc`. La suite (`exec/7-window-workers`) n'a lieu
  que si l'essai passe les critères de §4.
- **Réserve :** l'ADR **0133** pour la décision « on y va » ou « on
  s'arrête », écrite par `exec/6` avec les mesures. Si on y va, cet ADR 0133
  remplace l'ADR 0070 §1 pour les fenêtres.

## Contexte

Une app à fenêtre tourne sur le fil principal parce qu'un worker n'a pas de
DOM (ADR 0070 §1). `HackBackground` (ADR 0128) sort les calculs, pas la
logique de l'interface. Un serveur d'affichage (X11, Wayland) montre une autre
voie : le client calcule ce qu'il faut dessiner, le serveur dessine et renvoie
les entrées. Blazor fait déjà cela en mode serveur (lots de rendu envoyés au
navigateur par SignalR), mais le format binaire de ces lots
(`RenderBatchWriter`) et l'applicateur JavaScript sont internes au framework.

## Décision

### 1. Ce que l'essai construit

- Dans un worker : un `Renderer` dérivé de la classe publique
  `Microsoft.AspNetCore.Components.RenderTree.Renderer` (.NET 10), qui rend le
  composant racine de la fenêtre et sérialise chaque `RenderBatch` (types
  publics `RenderTreeFrame`, `RenderTreeEdit`) dans un format **à nous**,
  versionné, encodé par `WireCodec`.
- Sur le fil principal : un hôte de fenêtre de la plateforme qui reçoit les lots
  et les applique au DOM du contenu de la fenêtre par un petit module
  JavaScript propre au composant (`RemoteWindowHost.razor.js`, AGENTS.md I.2),
  puis renvoie au worker les événements du DOM (`click`, `input`, `change`,
  `keydown`, `focus`…) et les valeurs liées.
- Le chrome (barre de titre, bordures, boîte de l'ADR 0127) reste dessiné par
  le fil principal : une fenêtre qui gèle dans son worker se déplace et se ferme
  toujours.
- Sujet de l'essai : la **Calculatrice** (aucune interop JavaScript propre, CSS
  isolé, clavier).

### 2. Ce que l'essai ne fait pas

- Pas de réflexion sur des membres privés du framework, pas d'appel aux API
  internes de `blazor.web.js` : c'est la condition pour survivre aux mises à
  jour de .NET.
- Pas d'interop JavaScript d'une app dans le worker : une app qui en a
  (CodeEditor et Monaco, Terminal et xterm.js) reste sur le fil principal
  quoi qu'il arrive.
- Pas de threads .NET WebAssembly (`WasmEnableThreads`, refusé par Benoit,
  question Q8).

### 3. Mesures demandées

Sur Chromium, profil de référence de `build/verify-hosts.sh`, en Release
trimmé (ADR 0106) :

- latence ajoutée entre un clic et l'image suivante (p50, p95), comparée à la
  même Calculatrice sur le fil principal ;
- mémoire d'un worker de fenêtre (runtime et assemblies) ;
- temps d'ouverture de la fenêtre ;
- taille moyenne d'un lot de rendu ;
- couverture : CSS isolé, `@bind`, focus et `Tab`, raccourcis du gestionnaire
  de fenêtres (ADR 0095), fenêtre détachée (ADR 0078), dialogue du SDK.

### 4. Critères pour y aller

L'ADR 0133 conclut « on y va » si **tous** sont tenus ; sinon « on s'arrête »
et rien ne change pour les apps.

| Critère | Seuil |
|---|---|
| Latence ajoutée, p95 | ≤ 16 ms (une image à 60 Hz) |
| Mémoire d'un worker de fenêtre | ≤ 40 Mio |
| Ouverture de la fenêtre | ≤ 1 s de plus que sur le fil principal, worker déjà prêt (ADR 0071) |
| Tests UI de la Calculatrice | tous verts, sans modification de l'app |
| Accessibilité | suite axe et clavier verte |
| Framework | aucune API interne ou privée utilisée |

## Conséquences

- `exec/6` livre un prototype derrière un réglage d'hôte coupé par défaut,
  les mesures dans [`worker-processes.md`](../worker-processes.md) et l'ADR
  0133.
- Si on y va, `exec/7` permet `AppExecutionHost.Dedicated` aux fenêtres sans
  interop JavaScript propre (le validateur du manifeste l'interdit
  aujourd'hui) et fait l'inventaire des apps de base.

## Références

- ADR 0070, 0071, 0078, 0095, 0106, 0127, 0128.
- Protocole X11, protocole Wayland ; `Renderer` et `RenderBatch` d'ASP.NET Core.
