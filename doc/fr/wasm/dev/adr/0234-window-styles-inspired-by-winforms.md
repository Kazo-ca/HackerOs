> [English version](../../../../en/wasm/dev/adr/0234-window-styles-inspired-by-winforms.md)

# ADR 0234 : styles de fenêtre inspirés de WinForms

## Statut

Acceptée le 2026-11-07. Benoit a répondu aux questions 1 à 6 (fil « Préparer
la prochaine version de Kazo OS », voir « Questions posées à Benoit ») : les
recommandations, sauf la question 6 (réponse B) ; pour la question 4 (réponse
A), la version ne sort pas sans les fenêtres `None` transparentes.

- **Demandée par :** Benoit, 2026-11-07 (points 2 et 5 de sa réponse) : trois
  styles de fenêtre `Normal`, `Tool`, `None` et six options booléennes, comme
  `FormBorderStyle`, `ShowIcon`, `MaximizeBox`, `MinimizeBox`, `Modal` et
  `ShowInTaskbar` d'une `Form` WinForms.
- **Règle aussi :** [#384](https://github.com/Kazo-ca/Os/issues/384) (une app
  réduit sa propre fenêtre) ; [#378](https://github.com/Kazo-ca/Os/issues/378)
  (fenêtres sans cadre et transparentes).
- **Complète :** l'[ADR 0095](0095-window-events-sizes-and-keyboard.md) §2
  (tailles déclarées, `WindowOptions`) et
  l'[ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) §2 (`IAppWindowGateway`,
  liée à la seule fenêtre de l'app, sans capacité).
- **Ne change pas :** l'apparence d'une app qui ne déclare rien (style
  `Normal`, tous les booléens à leur défaut) ; les fenêtres détachées
  ([ADR 0078](0078-detached-display-windows.md)), qui restent des fenêtres du
  navigateur.

## Contexte

- Une app fenêtrée a **une seule fenêtre** par instance. Ses options sont
  déclarées dans le manifeste (`WindowOptions` : tailles, minimum, maximum,
  `Resizable`, `RememberGeometry`) et ne changent pas pendant l'exécution.
- La barre de titre (`WindowChrome.razor`) montre toujours l'icône, le titre,
  réduire, agrandir (si redimensionnable) et fermer. Toute fenêtre a son bouton
  dans la barre des tâches.
- Le gestionnaire de fenêtres sait déjà faire une fenêtre modale à sa
  propriétaire (`WindowModality.OwnerModal`), mais seuls les dialogues de l'OS
  s'en servent (MessageBox, ouvrir, enregistrer…).
- Une app qui dessine ses propres boutons (Webamp) ne peut ni réduire sa
  fenêtre (#384) ni se passer du cadre de l'OS (#378).

## Décision

### 1. Le style : `WindowStyle`

Nouvelle énumération `WindowStyle` dans `Kazo.Os.App.Abstractions` :

| Style | Barre de titre | Bordure et poignées | Boutons |
|---|---|---|---|
| `Normal` (défaut) | oui | oui | selon les booléens |
| `Tool` | oui, plus basse | non | fermer seulement |
| `None` | non | non | aucun : l'app dessine les siens |

- `Tool` force `Resizable`, `MaximizeBox` et `MinimizeBox` à faux, comme
  `FixedToolWindow` de WinForms. Il sert aussi aux dialogues de l'OS (§6).
- `None` montre seulement la zone cliente. L'utilisateur ne la déplace ni ne la
  redimensionne avec le cadre ; l'app le fait par l'API (§4). Son fond est
  transparent et les clics passent à travers les zones où l'app ne dessine rien
  (§5, question 4).
- Une fenêtre `None` garde tout le reste : bouton dans la barre des tâches,
  aperçu ([ADR 0199](0199-taskbar-window-previews.md)), Alt+Tab, menu de
  l'icône par Alt+Espace, animations ([ADR 0198](0198-window-animations.md)),
  « Autre écran » ([ADR 0178](0178-extended-screens-in-display-surfaces.md)).

### 2. Les options booléennes

Ajoutées à `WindowOptions`, avec les défauts donnés par Benoit :

| Option | Défaut | Effet |
|---|---|---|
| `ShowIcon` | `true` | Icône de l'app dans la barre de titre (le menu de l'icône reste sur clic droit dans la barre et sur Alt+Espace) |
| `Resizable` | `true` | Existe déjà : poignées, agrandir, ancrage et commandes clavier de taille |
| `MaximizeBox` | `true` | Bouton agrandir ; faux retire aussi le double-clic sur la barre et l'ancrage plein écran |
| `MinimizeBox` | `true` | Bouton réduire ; faux retire aussi « Réduire » du menu de l'icône et la commande clavier |
| `ShowInTaskbar` | `true` | Bouton dans la barre des tâches ; faux : la fenêtre n'a plus de bouton réduire et ne peut pas être réduite par l'utilisateur (question 6) ; elle reste dans Alt+Tab et le sélecteur |
| `Modal` | `false` | La fenêtre bloque sa propriétaire jusqu'à sa fermeture (§3) |

Les combinaisons suivent WinForms : un style qui retire un bouton gagne sur le
booléen, et `ShowInTaskbar = false` retire le bouton réduire comme
`MinimizeBox = false`. Le manifeste n'écrit un champ que s'il diffère de son défaut, pour que
les manifestes existants restent identiques octet pour octet (comme
`rememberGeometry`, [ADR 0116](0116-window-geometry-restore.md)).

### 3. Fenêtres enfants et `Modal` (question 3)

`WindowAppBase` gagne l'équivalent de `Form.Show` et `Form.ShowDialog` :

```csharp
protected ValueTask<IKazoWindow> ShowWindowAsync<TComponent>(KazoWindowRequest request, CancellationToken cancellationToken = default);
protected ValueTask<KazoDialogResult> ShowDialogAsync<TComponent>(KazoWindowRequest request, CancellationToken cancellationToken = default);
```

- `KazoWindowRequest` porte le titre, les tailles, le style, les booléens et
  les paramètres du composant. `ShowDialogAsync` impose `Modal = true`.
- La fenêtre enfant appartient à l'instance de l'app (même processus, mêmes
  droits) et a pour propriétaire la fenêtre qui l'ouvre. Elle se ferme avec son
  app. Une fenêtre modale suit sa propriétaire d'un écran à l'autre
  (ADR 0178 §2).
- Le composant enfant hérite de `KazoWindowBase` (nouvelle classe, sans le
  cycle de vie d'app de `WindowAppBase`), qui donne `CloseAsync(result)` et les
  mêmes méthodes de fenêtre que §4.

### 4. Ce que l'app peut demander à sa fenêtre (#384)

`IAppWindowGateway`, `WindowAppBase` et `KazoWindowBase` gagnent, sans
capacité, car elles n'atteignent que la fenêtre de l'app (ADR 0117 §2) :

- `MinimizeWindowAsync`, `MaximizeWindowAsync`, `RestoreWindowAsync` ;
- `SetWindowStyleAsync(WindowStyle)` et `SetWindowOptionsAsync(...)` pour
  changer style et booléens pendant l'exécution (question 2) ;
- `MoveWindowAsync` et `ResizeWindowAsync`, bornés à la zone de travail de
  l'écran et aux tailles déclarées ;
- l'attribut `data-kazo-window-drag` sur un élément de l'app : appuyer dessus
  déplace la fenêtre, comme sa barre de titre (pour `None`).

Une fenêtre détachée (ADR 0078) est une fenêtre du navigateur : ces demandes y
sont sans effet et renvoient faux. Les booléens règlent les boutons montrés à
l'utilisateur, pas ce que l'app peut faire : une app avec
`MinimizeBox = false` peut quand même réduire sa fenêtre, comme `WindowState`
en WinForms.

### 5. Transparence de `None` (question 4)

- La surface d'une fenêtre `None` n'a ni fond ni ombre ; la zone cliente reçoit
  `pointer-events: none`, et chaque élément dessiné par l'app reprend la
  souris (`pointer-events: auto`, appliqué aux enfants directs et à tout
  élément marqué `data-kazo-window-hit`).
- Un clic sur une zone vide atteint ce qui est dessous (bureau ou autre
  fenêtre), comme une fenêtre en couches de Windows.
- Ce point est un chantier séparé, mais la version 3.1.0 ne sort pas sans lui
  (réponse de Benoit à la question 4).

### 6. Dialogues de l'OS (question 5)

MessageBox, ouvrir, enregistrer, choisir un dossier, saisie de texte, choix
d'app et « ne répond pas » passent au style `Tool` avec `ShowInTaskbar = false`
(ils sont déjà modaux à leur propriétaire). Sur mobile, ils restent en plein
écran ([ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md)).

### 7. SDK et version (question 1)

Toute l'API va dans `PublicAPI.Unshipped.txt` ; la version est **3.1.0**
(mineure : ajout sans rupture). La fixture `Tests/fixtures/sdk-compat` doit
continuer à compiler sans changement.

## Conséquences

- Une app existante ne change pas. Une app peut avoir des fenêtres enfants et
  des dialogues à elle, avec le même cadre que l'OS.
- Webamp peut réduire sa fenêtre (#384) puis flotter sur le bureau (#378).
- La barre des tâches, Alt+Tab, les aperçus et le menu de l'icône lisent les
  nouvelles options ; les commandes clavier (ADR 0095 §3) respectent
  `MinimizeBox`, `MaximizeBox` et `Resizable`.

## Chantiers (série `os-3-1/base`)

1. `os-3-1/1-adr-window-styles` : cette ADR.
2. `os-3-1/2-file-dialog-home` : #385 (PR #389).
3. `os-3-1/3-capabilities-on-install` : #386 (PR #391).
4. `os-3-1/4-window-style-options` : §1, §2, §4 (#384) et §6.
5. `os-3-1/5-child-windows` : §3.
6. `os-3-1/6-transparent-none` : §5 (#378).
7. `os-3-1/7-release-3-1-0` : `Versions.props`, `collect-changes.sh`,
   `Shipped`, `features.md` ; puis la PR `os-3-1/base` vers `main`, que Benoit
   fusionne quand il le décide.

## Questions posées à Benoit

Posées le 2026-11-07 dans le fil, chacune avec sa recommandation (⭐).
Réponses de Benoit le 2026-11-07 : 4 A (« on ne doit pas faire une release sans
cette fonctionnalité »), 6 B, les autres selon la recommandation.

1. Version : A ⭐ 3.1.0 ; B 3.0.1.
2. Où régler style et booléens : A ⭐ manifeste et en cours d'exécution ;
   B manifeste seulement.
3. `Modal` : A ⭐ fenêtres enfants `ShowWindowAsync`/`ShowDialogAsync` ;
   B modal quand une app en lance une autre ; C pas de modal pour les apps
   (issue `TODO`).
4. `None` : A ⭐ transparent, clics à travers, ferme #378 ; B opaque seulement.
5. Dialogues de l'OS en `Tool` : A ⭐ tous ; B MessageBox et saisie seulement ;
   C aucun.
6. `ShowInTaskbar = false` et réduite : A ⭐ reste dans Alt+Tab et le
   sélecteur ; B pas de bouton réduire.
