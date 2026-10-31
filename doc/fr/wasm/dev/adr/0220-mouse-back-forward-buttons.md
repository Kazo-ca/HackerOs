> [English version](../../../../en/wasm/dev/adr/0220-mouse-back-forward-buttons.md)

# ADR 0220 : boutons Précédent et Suivant de la souris et Retour du système envoyés à l'app active

## Statut

Proposée le 2026-10-31. Les questions Q1 à Q3 sont posées à Benoit dans le
fil du projet ; les recommandations sont prises en attendant ses réponses.
Série `mouse-back/base` (branches `mouse-back/<n>-<sujet>`).

- **Complète :** le contrat `IAppBackHandler` du SDK
  ([`mobile-interface-platform-plan.md`](../old/mobile-interface-platform-plan.md)
  §8, `MOB-012`), la séquence Retour mobile (§7.3, `MOB-011`) et
  [ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md) (File
  Explorer mobile).
- **Ne change pas :** le bouton ◁ de la barre mobile, le bouton Retour de la
  barre de titre d'une fenêtre qui déclare `supportsBack`, ni les flèches de
  la barre d'outils de File Explorer et du navigateur.

## Contexte

- Le bouton Précédent d'une souris (bouton 4, `MouseEvent.button === 3`) et le
  bouton Suivant (bouton 5, `button === 4`) font reculer ou avancer **l'onglet
  du navigateur hôte**. Dans HackerOS, cela quitte l'OS (ou recharge la page
  précédente) au lieu de reculer dans l'app active. Même chose pour Alt+← et
  Alt+→, pour le bouton ou le geste Retour d'Android et pour le glissement
  depuis le bord gauche dans Safari sur iOS.
- Le SDK a déjà le contrat d'une app qui sait reculer :
  `IAppBackHandler` (`CanNavigateBack`, `CanNavigateBackChanged`,
  `NavigateBackAsync(AppBackRequest)`) avec `AppBackSource`
  (`SystemNavigationBar`, `DesktopChromeButton`, `KeyboardShortcut`). Le
  moteur de rendu des fenêtres enregistre tout composant qui l'implémente
  dans `AppBackHandlerRegistry`, que l'app déclare `supportsBack` ou non.
- Aujourd'hui, seul File Explorer **mobile** implémente `IAppBackHandler`. File
  Explorer bureau (`FileExplorerState` a déjà une pile Précédent et une pile
  Suivant) et le navigateur (`GoBack`, `GoForward`, Alt+←/→ dans sa fenêtre)
  ont leur propre historique, mais rien ne leur envoie le bouton de la souris.
- Les pages du navigateur s'affichent dans une iframe : les événements de la
  souris et du clavier qui s'y produisent n'arrivent pas au document de l'OS.
  Le pont de la page (`PageBridge.js`) relaie déjà des événements vers la
  fenêtre du navigateur.
- Chrome et Edge annulent la navigation quand la page appelle
  `preventDefault()` sur le `mouseup` des boutons 4 et 5 ; Firefox et Safari ne
  le garantissent pas. Le geste Retour d'un téléphone ne produit aucun
  événement de souris : seul l'historique du navigateur (`popstate`) le voit.

## Décision

### 1. Deux façons de capter le Retour : la souris et l'historique (Q1)

Un module `hardwareNavigation.js` (dans `wwwroot/` de
`HackerOs.Platform.Blazor`, importé par le service du §2) écoute le document
de l'OS et ne contient aucune règle : il traduit un geste en « Précédent » ou
« Suivant » avec sa source, et appelle C#.

- **Souris** : `mouseup` (phase de capture, sur `window`) avec
  `button === 3` → Précédent, `button === 4` → Suivant, source
  `MouseButton`. Le module appelle `preventDefault()` et arrête la
  propagation sur `mousedown`, `mouseup` et `auxclick` de ces deux boutons,
  ce qui annule la navigation de Chrome et Edge.
- **Clavier** : `keydown` Alt+← / Alt+→ et les touches `BrowserBack` /
  `BrowserForward`, source `KeyboardShortcut`, avec `preventDefault()`.
- **Historique** : après le premier geste de l'utilisateur dans l'OS (Chrome
  saute les entrées ajoutées sans geste de l'utilisateur), le module ajoute
  une entrée « sentinelle » avec `history.pushState` sur la même adresse, en
  gardant l'état existant de `history.state` (Blazor y range le sien). Quand
  le navigateur recule sur cette entrée (`popstate`), le module remet la
  sentinelle et envoie Précédent avec la source `SystemBack`. Son écouteur
  `popstate` est posé le premier, en phase de capture, et arrête la
  propagation pour les retours sur la sentinelle : le routeur de Blazor ne
  les voit pas.
- **Pas de double Retour** : si la souris ou le clavier vient d'envoyer
  Précédent (moins de 500 ms), le `popstate` qui suit (Firefox, Safari) remet
  la sentinelle sans renvoyer Précédent.
- L'historique ne sert qu'au Précédent : avancer demanderait une deuxième
  sentinelle « devant », fragile. Le Suivant vient de la souris et du clavier.

### 2. `HardwareNavigationService` envoie le geste à la fenêtre active

Un service scoped de `HackerOs.Platform.Blazor` reçoit les appels du module et
choisit la cible :

- **Bureau** : la fenêtre qui a le focus (pas réduite). Précédent appelle son
  `IAppBackHandler`, Suivant son `IAppForwardHandler` (§3). Si la fenêtre n'en
  a pas ou répond `NotHandled`, il ne se passe rien (Q3). Un menu contextuel
  ou le menu Démarrer ouvert se ferme d'abord, comme avec Échap.
- **Mobile** : Précédent suit la séquence existante du bouton ◁
  (`MobileNavigationCommandsAdapter.RequestBack` : volet ou Récents ouverts,
  dialogue annulable, `IAppBackHandler` de l'app, fenêtre précédente,
  fermeture, accueil) avec la source du geste. À l'accueil, il ne se passe
  rien : on ne quitte pas l'OS (Q3). Suivant appelle l'`IAppForwardHandler`
  de l'app au premier plan.
- Le module est attaché par `DesktopShell` et `MobileShell` (une seule fois
  par document ; une fenêtre détachée ou un écran supplémentaire a son propre
  document et son propre module, qui vise la fenêtre active de cet écran).

### 3. SDK : deux sources de plus et `IAppForwardHandler` (Q2)

Ajouts dans `HackerOs.AppSdk.Blazor` (`PublicAPI.Unshipped.txt`, prochaine
mineure), sans rien changer à l'existant :

- `AppBackSource.MouseButton` : bouton Précédent ou Suivant de la souris ;
  `AppBackSource.SystemBack` : Retour du navigateur ou du téléphone (bouton
  ou geste Android, glissement iOS, flèche du navigateur hôte).
- `IAppForwardHandler`, sur le modèle d'`IAppBackHandler` :
  `CanNavigateForward`, `event Action? CanNavigateForwardChanged`,
  `ValueTask<AppBackResult> NavigateForwardAsync(AppForwardRequest request,
  CancellationToken cancellationToken = default)` ;
  `AppForwardRequest(AppBackSource Source)` reprend l'énumération des
  sources (son nom reste `AppBackSource`, déjà publié).
- `AppBackHandlerRegistry` suit aussi les `IAppForwardHandler` (même
  enregistrement par référence) ; `WindowAppRenderer` enregistre tout
  composant qui implémente l'une ou l'autre interface.
- Une app n'a rien à déclarer dans son manifeste : implémenter l'interface
  suffit. `supportsBack` garde son seul rôle (bouton Retour dans la barre de
  titre et validation à la découverte).

### 4. Applications branchées

- **File Explorer bureau** : implémente `IAppBackHandler` et
  `IAppForwardHandler` sur `FileExplorerState` (mêmes actions que les flèches
  de sa barre d'outils). Il ne déclare pas `supportsBack` : sa barre d'outils
  a déjà ses flèches.
- **File Explorer mobile** : garde sa séquence Précédent et implémente
  `IAppForwardHandler`.
- **Navigateur** : implémente les deux interfaces sur son historique
  (`GoBack`, `GoForward`). `PageBridge.js` relaie les boutons 4 et 5 et
  Alt+←/→ faits dans la page (avec `preventDefault()` dans l'iframe) ; la
  fenêtre appelle alors directement son propre Précédent ou Suivant. Son
  raccourci Alt+←/→ actuel passe par le même chemin.
- **Paramètres et Hack Store** : quand ils affichent une sous-page (section
  sur mobile, fiche d'une app), Précédent revient à la liste. Vérifié pendant
  le chantier ; une app sans page à reculer n'implémente rien.
- Les autres apps (terminal, éditeurs, calculatrice…) n'ont pas d'historique
  de navigation : le geste ne fait rien. Annuler une modification reste
  Ctrl+Z.

### 5. Limites connues

- Une page affichée dans une autre iframe (site ASP.Hack dans une app autre
  que le navigateur, contenu tiers) garde le comportement du navigateur hôte
  pour la souris ; le Retour par l'historique, lui, fonctionne partout.
- Sur iOS, une app installée sur l'écran d'accueil n'a pas de geste Retour
  système : le bouton ◁ de la barre mobile reste le moyen de reculer.
- L'utilisateur peut toujours quitter HackerOS par la barre d'adresse ou le
  menu du bouton Précédent du navigateur hôte (appui long).

## Questions posées à Benoit

Posées dans le fil du projet le 2026-10-31 ; recommandations prises en
attendant.

1. **Mécanisme.** A (recommandé) : souris et historique (§1). B : historique
   seulement, un seul mécanisme mais pas de Suivant fiable ni de source
   connue. C : souris seulement, rien ne change sur mobile.
2. **Bouton Suivant.** A (recommandé) : oui, `IAppForwardHandler` (§3). B :
   Précédent seulement.
3. **Quand l'app n'a plus rien à reculer.** A (recommandé) : on ne quitte
   jamais l'OS (§2). B : on laisse le navigateur reculer, ce qui quitte
   HackerOS.

## Conséquences

- Le bouton Précédent de la souris, Alt+← et le Retour du téléphone reculent
  dans l'app active au lieu de quitter HackerOS.
- Toute app du SDK en profite en implémentant `IAppBackHandler` (déjà
  publié) et, si elle veut, `IAppForwardHandler`.
- Une entrée de plus dans l'historique de l'onglet : le bouton Précédent du
  navigateur hôte ne quitte plus l'OS d'un seul clic.

## Chantiers

- [x] `mouse-back/0-adr` : cette ADR, index, mémoire, `features.md`.
- [ ] `mouse-back/1-platform` : `hardwareNavigation.js`,
  `HardwareNavigationService`, sources `MouseButton` et `SystemBack`,
  `IAppForwardHandler`, registre, routage bureau et mobile, tests.
- [ ] `mouse-back/2-apps` : File Explorer (bureau et mobile), navigateur et
  `PageBridge.js`, Paramètres et Hack Store, tests, doc des apps et du SDK.
