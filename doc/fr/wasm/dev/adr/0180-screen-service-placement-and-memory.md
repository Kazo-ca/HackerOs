> [English version](../../../../en/wasm/dev/adr/0180-screen-service-placement-and-memory.md)

# ADR 0180 : service d'écrans, placement et mémoire des écrans

## Statut

Accepté le 2026-10-28, chantier `multi-monitor/3-screen-service` du
[plan multi-moniteur](../plans/multi-monitor-plan.md). Précise
l'[ADR 0178 §4](0178-extended-screens-in-display-surfaces.md) (service d'écrans) et
le format `[screens]` de l'[ADR 0179 §7](0179-per-screen-taskbars-and-other-screen-menus.md).
Mise en œuvre : [`screen-service.md`](../screen-service.md).

## Contexte

- L'ADR 0178 §4 fixe le but : une surface par écran, sur un moniteur libre sous
  Chrome et Edge avec la permission `window-management`, un repli ailleurs avec la
  position retenue, le plein écran par bouton, « Restaurer les écrans » après un
  rechargement, un bandeau quand l'onglet principal est caché.
- Le navigateur impose trois contraintes. Une fenêtre ne s'ouvre que pendant un
  geste, et `window.open` consomme ce geste. La demande de permission
  (`getScreenDetails()`) a aussi besoin du geste, et sa réponse arrive quand
  l'utilisateur répond. Aucun événement ne signale qu'une fenêtre a été déplacée.
- L'exemple de l'ADR 0179 §7 (`2=1920,0,1920,1080`) mélange deux choses : les
  écrans à rouvrir et la place de chaque écran. Un écran fermé par l'utilisateur
  ne doit plus être proposé, mais sa place doit rester pour la prochaine ouverture.

## Décision

1. **Service.** `ScreenService` (`HackerOs.Platform.Blazor.Display`) est un service
   de l'OS, une instance par session comme `WindowRuntime`. Ses types sont publics
   pour les assemblys de l'OS (`PublicAPI.Unshipped.txt`), mais aucune API du SDK
   des apps ne les expose (réponse de Benoit du 2026-10-28 : plus tard). Il ouvre
   la surface par `DisplaySurfaceService` (méthode interne avec placement), puis
   envoie `AddDisplayCommand`, `ChangeViewportCommand` et `RemoveDisplayCommand` à
   `WindowRuntime`.
2. **Choix du moniteur avant `window.open`.** Le module `screens.js` décide où va
   la fenêtre, dans le même geste :
   - permission accordée : le premier moniteur libre de gauche à droite (ni celui
     de l'onglet principal, ni celui d'un autre écran), zone utile complète ;
   - permission à demander : la fenêtre s'ouvre à sa place retenue (ou en
     1280 × 800) et la demande part dans le même geste ; si l'utilisateur accepte,
     la fenêtre est déplacée sur un moniteur libre (`moveTo`, `resizeTo`) ;
   - permission refusée ou API absente (Firefox, Safari) : repli, la fenêtre
     s'ouvre à sa place retenue et l'utilisateur la place.
3. **Place retenue.** La place d'un écran est lue chaque seconde
   (`screenX`, `screenY`, `innerWidth`, `innerHeight` : les valeurs que
   `window.open` reprend) et écrite une seconde après le dernier changement. En
   plein écran, elle n'est pas lue.
4. **`screens.config`, section `[screens]`.** Une clé `open` liste les écrans
   ouverts, dans leur ordre ; chaque ligne numérotée garde la place de son écran,
   même après sa fermeture :

   ```ini
   [screens]
   open = 2,3
   2 = 1920,0,1920,1080
   3 = -1280,0,1280,1024
   ```

   Les numéros vont de 2 à 9 : au plus neuf écrans. Le service écrit la couche
   utilisateur (`~/.config/hackeros/screens.config`). Le chantier 7 ajoute
   `[taskbar]` et la page Paramètres ▸ Écrans.
5. **Ce qui est restauré.** Un écran fermé par l'utilisateur, perdu, retiré par
   `CloseScreenAsync` ou dont le moniteur est débranché sort de `open`. Un
   rechargement, la fermeture de l'onglet ou une fermeture de session gardent
   `open` tel quel : la session suivante propose de les rouvrir.
6. **« Restaurer les écrans ».** Après la connexion, si `open` liste des écrans,
   une carte au-dessus de la barre des tâches de l'écran 1 propose **Restaurer les
   écrans** et **Ignorer**. Restaurer rouvre les écrans dans l'ordre, avec leur
   numéro et leur place. Un navigateur peut n'accepter qu'une fenêtre par clic :
   les écrans bloqués restent proposés pour le clic suivant, et la notification
   « fenêtre bloquée » n'est postée que si aucun écran ne s'est ouvert. Ignorer, ou
   ouvrir un écran à la main, retire l'offre.
7. **Plein écran.** Un bouton `⤢` en haut à droite de chaque écran secondaire
   appelle `requestFullscreen` sur le document de cet écran, pendant le clic fait
   dans cet écran. Il n'est jamais imposé ; `Échap` en sort.
8. **Onglet principal caché.** `visibilitychange` sur l'onglet principal affiche
   un bandeau en haut de chaque écran secondaire tant qu'il est caché.
9. **Moniteur débranché.** Sur `screenschange`, un écran dont le moniteur a disparu
   est fermé par le service : ses fenêtres reviennent sur l'écran 1 (ADR 0178 §2).
10. **Zone de travail.** Jusqu'au chantier 4, la zone de travail d'un écran est la
    taille intérieure de sa fenêtre. Le chantier 4 la confie au `DesktopArea` de
    l'écran, qui retire sa barre des tâches.

## Conséquences

- Sans écran secondaire, rien ne change : le service lit `screens.config`, ne
  l'écrit pas et n'affiche rien.
- Les menus « Nouvel écran » (chantier 6) et la page Paramètres ▸ Écrans
  (chantier 7) appellent `OpenScreenAsync`, `RestoreScreensAsync`,
  `CloseScreenAsync` et `ToggleFullScreenAsync` pendant un clic.
- Le contrat des surfaces du SDK (`IDisplaySurfaceService`) ne change pas ; le
  placement passe par une méthode interne de `DisplaySurfaceService`.

## Vérification

- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs` : écran 2
  enregistré avec sa taille, zone suivie au redimensionnement, écran fermé par
  l'utilisateur, par l'OS, moniteur débranché, restauration dans l'ordre à la place
  retenue, fenêtre bloquée, offre ignorée, place écrite après une pause, états
  (onglet caché, plein écran, permission), hôte sans JavaScript.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ScreensSettingsTests.cs` : lecture et
  écriture de `[screens]`.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs` (Chromium réel, repli) :
  offre après rechargement, écran 2 ouvert à sa taille retenue, écran fermé à la
  main non proposé ensuite.

## Tâches

- [x] `ScreenService`, `screens.js`, placement dans `displaySurfaces.js`.
- [x] `[screens]` de `screens.config` (`ScreensSettingsDocuments`).
- [x] Carte « Restaurer les écrans », bouton plein écran, bandeau onglet caché.
- [x] Tests unitaires et UI.E2E, docs et index.
