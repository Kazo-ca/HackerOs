> [English version](../../../../en/wasm/dev/adr/0127-priority-and-not-responding-dialog.md)

# ADR 0127 : priorité `nice` sans quantum, et boîte « L'application ne répond pas : Attendre | Terminer l'application »

## Statut

Accepté le 2026-10-18.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décisions 5 et 6 (réponses Q3 et Q4) ; mis en œuvre par
  `exec/4-not-responding-ui`.
- **Complète :** l'ADR 0067 §4 (« l'OS ne fait rien de plus » : il affiche
  désormais la boîte décrite ici), l'ADR 0069 §3 (escalade), l'ADR 0071 (file
  du pool).

## Contexte

- Blazor WebAssembly n'a qu'un thread .NET par runtime : rien ne peut préempter
  une tranche. Benoit a refusé un quantum imposé (Q4) : il serait trompeur, car
  il ne toucherait que le code qui appelle `CheckpointAsync`.
- L'ADR 0067 marque un processus « Not responding » quand une tranche dépasse
  `UnresponsiveThresholdMs` (5 s) : pendant le blocage dans un worker, après
  coup sur le fil principal. L'OS ne propose rien à l'utilisateur.
- Benoit veut la boîte des navigateurs : « L'application ne répond pas :
  Attendre | Terminer l'application » (Q3), à la place d'un OOM killer actif
  par défaut.

## Décision

### 1. `nice` et `renice`

- Valeur `nice` de `-20` à `19`, `0` par défaut, héritée par les enfants.
  Commandes `nice [-n N] commande…` et `renice -n N -p pid…` ; baisser la
  valeur (plus prioritaire) demande un administrateur, comme Linux.
- Effet réel, et le seul : l'**ordre de la file du pool** (ADR 0071) et des
  tâches de l'ADR 0128 — la plus petite valeur passe d'abord, puis l'ordre
  d'arrivée — et le choix du worker quand plusieurs sont libres (un processus à
  `nice` positif évite le worker toujours prêt tant qu'un autre est libre). Sur
  le fil principal, `nice` n'a aucun effet ; la documentation le dit.
- Affichage : colonne `NI` de `ps -l`, `ps -o ni` et `top`/`htop`, champ 19 de
  `/proc/<pid>/stat`, colonne « Priorité » (masquée par défaut) du gestionnaire
  de tâches.

### 2. Quand une fenêtre « ne répond pas »

Le gestionnaire de fenêtres marque la fenêtre d'un processus **sans réponse**
dans l'un de ces cas :

1. le processus est « Not responding » au sens de l'ADR 0067 §4 (tranche de
   plus de 5 s ; vu pendant le blocage quand la logique de la fenêtre est dans
   un worker, ADR 0129) ;
2. une entrée livrée à la fenêtre (clic, touche, demande de fermeture) attend
   depuis plus de 5 s pendant que **les tranches de ce processus** occupent le
   fil principal (boucle découpée en tranches courtes qui ne rend jamais la
   main aux événements de sa propre fenêtre) ;
3. sur le fil principal, une tranche de plus de 5 s **vient de finir** et
   l'utilisateur a essayé d'agir sur la fenêtre pendant le blocage : la boîte
   s'affiche à la fin de la tranche, comme Windows après un gel.

L'état cesse quand la fenêtre traite à nouveau ses entrées en moins du seuil.

### 3. Ce que voit l'utilisateur

- Le titre affiché reçoit le suffixe « (Ne répond pas) » / « (Not responding) »
  selon la langue de la session (ADR 0104). C'est un affichage du gestionnaire
  de fenêtres : le titre posé par l'app (`SetWindowTitleAsync`, ADR 0117) ne
  change pas et `WindowTitleChangedEvent` n'est pas levé.
- Le contenu de la fenêtre est grisé (sans animation si le mouvement réduit est
  demandé, ADR 0105).
- Une boîte attachée à cette fenêtre seulement (le reste du bureau reste
  utilisable) : « « Calculatrice » ne répond pas. Vous pouvez attendre qu'elle
  réponde ou terminer l'application. » avec **Attendre** (bouton par défaut,
  `Échap`) et **Terminer l'application**. `role="alertdialog"`, focus piégé,
  testée par la suite d'accessibilité.
- Cliquer la croix d'une fenêtre sans réponse ouvre la même boîte.
- **Attendre** ferme la boîte ; si la fenêtre ne répond toujours pas 5 s plus
  tard, elle revient.
- **Terminer l'application** suit l'escalade de l'ADR 0069 avec la raison
  `Terminated` et un délai de grâce de **2 s** (le processus est déjà sans
  réponse), puis l'étape 3, et l'étape 4 dans un worker. Les fenêtres
  détachées (ADR 0078) affichent la boîte dans leur propre surface.
- La boîte, le suffixe et le grisé sont des composants de la plateforme en
  Blazor natif avec CSS isolé (chrome de fenêtre, AGENTS.md II.1).

### 4. Ce que l'OS ne peut pas faire, et le dit

Une boucle sur le fil principal qui ne rend jamais la main gèle tout l'OS :
aucune boîte de HackerOS ne peut s'afficher, c'est la boîte du vrai navigateur
qui apparaît. [`cpu-accounting.md`](../cpu-accounting.md) et le guide du SDK
le disent et renvoient vers `HackBackground.RunAsync` (ADR 0128) et
`CheckpointAsync` (ADR 0069).

### 5. Réglages

`UnresponsiveThresholdMs` (ADR 0066) sert aux trois cas de §2. La boîte se
coupe dans `/etc/hackeros/window-manager.config` (`[unresponsive]
dialog=true`) ; coupée, il reste le suffixe et le grisé.

## Conséquences

- Aucune app à modifier. `ps`, `top`, `htop` et le gestionnaire de tâches
  gagnent `NI` ; `nice` et `renice` rejoignent les commandes.
- Tests : unité (détection des trois cas avec une horloge simulée), UI (boîte,
  Attendre, Terminer, croix), accessibilité (axe, clavier), parité de `nice`.
- Docs : [`cpu-accounting.md`](../cpu-accounting.md),
  [`window-model.md`](../../framework/window-model.md), [`task-manager.md`](../task-manager.md),
  [`linux-commands.md`](../linux-commands.md).

## Références

- ADR 0066, 0067, 0069, 0071, 0078, 0104, 0105, 0117, 0128, 0129.
- `nice(1)`, `renice(1)`, `proc(5)` (`stat`, champ 19).
