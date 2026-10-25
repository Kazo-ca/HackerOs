# ADR 0133 : rendu d'une fenêtre dans un worker, verdict de l'essai : on s'arrête

## Statut

Accepté le 2026-10-18 par `exec/6-remote-renderer-poc`, avec la recommandation
soumise à Benoit sur une carte de décision dans le fil « Exec 6 ». La
recommandation (option A ci-dessous) est appliquée en attendant sa réponse ;
s'il choisit une autre option, un nouvel ADR remplace celui-ci.

- **Conclut :** l'essai de l'[ADR 0129](0129-window-rendering-in-worker-trial.md).
- **Ne remplace pas** l'ADR 0070 §1 : les fenêtres restent sur le fil
  principal (le remplacement n'était prévu que si on y allait).
- **Doc :** [`remote-window-rendering.md`](../remote-window-rendering.md)
  (architecture de l'essai, format des opérations, mesures, liste des tâches) ;
  [`worker-processes.md`](../worker-processes.md) (messages `ui`, `uin`,
  `start` `k = 3`).

## Contexte

L'ADR 0129 demandait un prototype qui rend la Calculatrice dans un Web Worker,
des mesures sur la publication Release trimmée et un verdict selon six
critères, **tous** requis pour y aller. Le prototype est construit et marche :
la Calculatrice s'ouvre dans un worker dédié, se dessine à l'identique, répond
à la souris et au clavier, et se ferme proprement. Il reste dans le dépôt
derrière le réglage `HackerOs:RemoteWindowRendering:Enabled`, coupé par défaut.

## Mesures

Publication Release trimmée de l'Ecosystem (`artifacts/publish-trimmed`),
Chromium sans écran, 300 clics sur les touches de la Calculatrice, 6
ouvertures par mode. Test qui les produit :
`Measures_the_calculator_on_the_page_and_in_a_worker`
(`Tests/HackerOs.TrimmedPublish.E2E.Tests`), qui écrit
`remote-window-measurements.json`.

| Mesure | Sur la page (réglage coupé) | Dans un worker (réglage actif) | Écart |
|---|---|---|---|
| Ouverture, première | 140 ms | 606 ms | +466 ms |
| Ouverture, suivantes (worker prêt) | 42 à 54 ms (≈ 46) | 358 à 413 ms (≈ 385) | +≈ 340 ms |
| Clic → DOM modifié, p50 | 2,2 ms | 3,4 ms | +1,2 ms |
| Clic → DOM modifié, p95 | 4,1 ms | 7,4 ms | +3,3 ms |
| Clic → image suivante, p50 | 4 ms | 19,7 ms | une image plus tard au plus |
| Clic → image suivante, p95 | 20,2 ms | 20,1 ms | ≈ 0 |
| Mémoire linéaire du worker | 55,4 Mio (worker prêt, vide) | **66,5 Mio** | +11,1 Mio pour la fenêtre |
| Mémoire linéaire de la page | 138 Mio | 138 Mio | inchangée |
| Premier lot | — | 5 337 octets (85 nœuds) | |
| Lot suivant, moyenne | — | 72 octets, 140 µs d'encodage | |

La mémoire est celle de la mémoire linéaire WebAssembly du runtime du worker
(`HEAPU8.length`), la seule que le navigateur laisse lire dans un worker sans
isolation cross-origin (ADR 0131). `/proc/hackeros/workers` montre le tas
managé : 5 694 kio vide, 5 866 kio avec la fenêtre.

### Pourquoi 55 Mio pour un worker vide

Chaque worker charge tout le jeu d'assemblies du boot (`workerBootstrap.js`,
ADR 0070) : 87 assemblies, ≈ 20,3 Mo, plus 1,98 Mo d'assemblies de base.
`HackerOs.AppSdk.Icons` pèse à lui seul 8,67 Mo ; le runtime natif 3 Mo,
ICU 0,55 Mo. La fenêtre elle-même n'ajoute que ≈ 11 Mio. Le coût est donc
celui d'un runtime .NET de plus, pas du rendu.

## Critères de l'ADR 0129 §4

| Critère | Seuil | Résultat | Tenu |
|---|---|---|---|
| Latence ajoutée, p95 | ≤ 16 ms | +3,3 ms jusqu'au DOM, ≈ 0 jusqu'à l'image | oui |
| Mémoire d'un worker de fenêtre | ≤ 40 Mio | 66,5 Mio | **non** |
| Ouverture | ≤ 1 s de plus | +≈ 340 ms (worker prêt), +466 ms (premier) | oui |
| Tests UI de la Calculatrice | verts, app inchangée | test E2E dédié vert (clics, clavier, CSS, `Tab`, raccourcis, fermeture) ; app inchangée | oui |
| Accessibilité | axe et clavier verts | axe : aucune violation sérieuse ou critique ; `Tab` natif | oui |
| Framework | aucune API interne ou privée | aucune ; mais avertissement `BL0006` (voir plus bas) | oui, avec réserve |

## Décision

### 1. On s'arrête

Un critère n'est pas tenu (la mémoire, 66,5 Mio contre 40 Mio). Selon l'ADR
0129 §4, **`exec/7-window-workers` n'a pas lieu** et rien ne change pour les
apps :

- les fenêtres restent sur le fil principal (ADR 0070 §1, inchangé) ;
- le validateur du manifeste refuse toujours `AppExecutionHost.Dedicated` pour
  une fenêtre (`manifest.execution.window`) ;
- un calcul lourd d'une fenêtre sort par `HackBackground.RunAsync`
  (ADR 0128), qui est la voie recommandée.

### 2. Le prototype reste, coupé

Le code de l'essai reste dans le dépôt, sans effet tant que le réglage est
coupé :

- `Platform/HackerOs.Platform.RemoteRendering` (nouveau projet) : le
  `Renderer` du worker, le traducteur d'opérations DOM, l'hôte de fenêtre de la
  page et son module JavaScript collocalisé ;
- `HackerOs.Platform.WorkerHost` : messages `ui` et `uin`, `start` `k = 3`,
  `WorkerRemoteProcessHost.StartWindowAsync`, `RemoteWindowProcess` ;
- `HackerOs.Platform.Blazor` : `IRemoteWindowRendering`, consulté par
  `WindowAppRenderer` quand le service existe ;
- `appsettings.json` de l'Ecosystem : `HackerOs:RemoteWindowRendering`
  (`Enabled: false`, `Apps: ["org.hackeros.calculator"]`).

Le garder coûte peu (aucun service enregistré quand le réglage est coupé) et
permet de refaire les mesures si le coût d'un worker baisse (option D).
Ces types sont des ajouts publics de la plateforme (`PublicAPI.Unshipped.txt`
de la plateforme), pas du SDK des apps : aucune app ne peut en dépendre.

### 3. Ce que l'essai a construit (pour la mémoire)

- **Le worker traduit, la page exécute** (modèle X11). Plutôt que d'envoyer
  le `RenderBatch` et de le rejouer côté page, le worker garde un miroir de
  l'arbre logique de Blazor (composants, régions aplaties, éléments, textes,
  markup) et écrit des opérations DOM sur des nœuds numérotés : créer un
  élément, un texte, du markup, retirer, poser ou retirer un attribut, changer
  un texte, écouter un événement, déplacer, drapeaux `preventDefault` et
  `stopPropagation`. La page n'a qu'un petit exécuteur
  (`RemoteWindowHost.razor.js`) ; toute la logique reste en C# (AGENTS.md I.2).
  L'ADR 0129 §1 parlait d'un format encodé par `WireCodec` : le format est bien
  à nous et versionné avec le C#, mais en JSON dans le message `ui`, comme les
  autres messages du protocole.
- **Événements** : la page renvoie `{h, n, a}` (numéro du gestionnaire, nom,
  propriétés de l'événement) ; le worker reconstruit les `EventArgs` de
  Blazor et, pour `input` et `change`, un `EventFieldInfo` pour que `@bind`
  ne réécrive pas ce que l'utilisateur tape.
- **Dialogues du SDK** : la boîte de message et la saisie de texte sont
  ouvertes par l'hôte de la page avec le contexte du processus ; la demande
  d'identifiants répond « annulé » (aucun mot de passe n'entre dans un
  worker) ; les dialogues de fichiers sont refusés.
- **Processus** : un worker dédié, pris au worker prêt (ADR 0071) et terminé à
  la fermeture ; la fenêtre a son pid, son contexte distant et ses passerelles
  (ADR 0070, 0124).

### 4. Couverture constatée

| Point de l'ADR 0129 §3 | Résultat |
|---|---|
| CSS isolé | identique au pixel (captures comparées) |
| Clavier, `Tab` | oui ; `Tab` est natif, le DOM est le vrai DOM de la page |
| Focus par programme (`FocusAsync`) | **non** : pas d'`IJSRuntime` dans le worker |
| Raccourcis du gestionnaire de fenêtres (ADR 0095) | oui (`Alt+F7`, `Alt+Q`) : le chrome reste sur la page |
| `@bind` | oui, testé par les tests unitaires (texte et case à cocher) |
| Dialogues du SDK | message et saisie relayés (tests unitaires) ; fichiers refusés |
| Fenêtre détachée (ADR 0078) | **non** : la surface détachée passe par l'interop JavaScript |
| Langue du processus | par construction (satellites `fr` chargés avant l'app, comme pour une commande) ; non vérifié dans un navigateur |

### 5. Risque relevé : `BL0006`

`RenderBatch`, `RenderTreeFrame`, `RenderTreeEdit` et `Renderer` sont publics,
mais l'analyseur de Blazor (`BL0006`) avertit qu'ils « changeront dans une
version future ». Le critère « aucune API interne » est tenu à la lettre ; en
pratique, une mise à jour de .NET peut casser le traducteur. Les fichiers qui
les lisent désactivent `BL0006` avec cette raison, et les tests unitaires
(`Tests/HackerOs.Platform.RemoteRendering.Tests`) servent de garde-fou à
chaque montée de version.

## Options soumises à Benoit

| Option | Ce qui se passe | Implications |
|---|---|---|
| **A. On s'arrête (recommandée, appliquée)** | `exec/7` n'a pas lieu ; prototype gardé, coupé. | Rien ne change pour les apps ; la charge lourde passe par `HackBackground`. Aucun coût mémoire. |
| B. Un worker partagé pour toutes les fenêtres en worker | `exec/7` réécrit l'essai : un seul runtime pour toutes les fenêtres « worker », ≈ 11 Mio par fenêtre ensuite. | Tient le critère mémoire à partir de la 2ᵉ fenêtre, mais une fenêtre qui boucle gèle toutes les autres fenêtres de ce worker : on perd l'isolation qui justifiait l'essai. Nouvel ADR pour changer le critère. |
| C. On y va quand même, un worker par fenêtre | `exec/7` comme prévu, en acceptant ≈ 66 Mio par fenêtre. | Isolation complète, mais 5 fenêtres coûtent ≈ 330 Mio de plus ; focus par programme et fenêtres détachées restent impossibles. Nouvel ADR qui relève le seuil. |
| D. D'abord alléger le jeu d'assemblies d'un worker | Chantier séparé : un worker ne charge que ce qu'il lui faut (sans `AppSdk.Icons`, etc.), puis on refait les mesures. | Profite aussi aux commandes et services en worker ; le verdict reste « on s'arrête » jusqu'à la nouvelle mesure. |

## Conséquences

- `exec/7-window-workers` est retiré de la série `exec` (plan des suites de
  l'étape 5, décision 7) ; `exec/9` et `exec/10` ne l'attendent plus.
- Le réglage `HackerOs:RemoteWindowRendering` reste documenté comme un essai,
  coupé par défaut, sans garantie.
- `build/verify-hosts.sh` et la publication trimmée sont vérifiés avec le
  réglage coupé et actif.

## Références

- ADR 0070, 0071, 0078, 0095, 0106, 0113, 0124, 0128, 0129, 0131, 0132.
- `Microsoft.AspNetCore.Components.RenderTree.Renderer` (.NET 10).
