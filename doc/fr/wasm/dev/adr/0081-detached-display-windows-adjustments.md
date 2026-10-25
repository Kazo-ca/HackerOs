> [English version](../../../../en/wasm/dev/adr/0081-detached-display-windows-adjustments.md)

# ADR 0081 : fenêtres détachées, ajustements de la mise en œuvre

## Statut

Accepté le 2026-10-16, avec le chantier `web/10-detached-window`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** ADR 0078 (fenêtres d'affichage détachées). Ne remplace aucune
  de ses décisions ; précise où vivent les contrats et tranche la preuve de son
  §4.

## Contexte

L'ADR 0078 place `IDisplaySurfaceService` dans la plateforme et laisse ouverte
une question : le moteur de rendu de Blazor peut-il écrire dans le DOM d'un
autre document et en recevoir les événements ? La réalisation a aussi montré
trois contraintes que l'ADR ne décrivait pas : la frontière du SDK, la façon
dont Blazor retrouve les éléments, et le pont des pages isolées de l'ADR 0075.

## Décision

### 1. Le contrat est dans le SDK Blazor, l'implémentation dans la plateforme

Le navigateur est une app de base : il n'obtient les services de l'OS que par
le SDK (ADR 0055, diagnostic `HOSAPP0031`). Le contrat (`IDisplaySurfaceService`,
`IDisplaySurface`, `DisplaySurfaceOptions`, `DisplaySurfaceOpenResult`) et le
composant `DisplaySurfaceContent` vont donc dans `HackerOs.AppSdk.Blazor`
(espace de noms `.Display`), comme `IDialogService`. L'implémentation
`DisplaySurfaceService` reste dans `HackerOs.Platform.Blazor` et
`AddHackerOsEcosystem` l'enregistre (portée de session, un circuit sur le
serveur). Le bureau est la surface principale implicite ; le service ne gère
que les autres.

### 2. Blazor dessine dans la fenêtre détachée par un portail (preuve réussie)

Le contenu d'une surface est rendu dans l'arbre de composants de l'instance
principale, puis son élément est déplacé dans la fenêtre détachée
(`DisplaySurfaceContent`). Le moteur garde ses références aux nœuds et continue
de les mettre à jour où qu'ils soient. Deux mécanismes de Blazor supposent
pourtant un seul document ; un initialiseur JS de la bibliothèque
(`HackerOs.Platform.Blazor.lib.module.js`, pris en charge par les trois hôtes
sans référence manuelle) les étend avant le démarrage de Blazor :

- **Événements.** Blazor délègue tous les événements par des écouteurs posés
  sur `document`. L'initialiseur retient chaque écouteur posé sur `document` et
  le pose aussi sur le document de chaque surface ouverte.
- **`@ref`.** Blazor retrouve l'élément d'un `ElementReference` par
  `document.querySelector('[_bl_<id>]')`. L'initialiseur cherche aussi dans les
  documents des surfaces quand ce sélecteur précis ne trouve rien.

Les éléments restent créés par le document principal, si bien que les tests
`instanceof Element` de Blazor tiennent. Le test E2E
`BrowserDetachedWindowTests` clique un bouton Blazor dans la fenêtre détachée :
il vérifie la preuve à chaque mise à jour de Blazor. Les sites Blazor de
l'ADR 0073 §4 pourront donc s'ouvrir en fenêtre détachée, et le deuxième écran
sera une surface qui affiche un bureau.

### 3. Les pages isolées gardent leur pont

Le pont d'une page (ADR 0075 §3) parle à son parent, qui devient la fenêtre
détachée :

- la fenêtre détachée remet à la fenêtre principale les messages qu'elle
  reçoit, où l'hôte de l'iframe les écoute ;
- le pont n'accepte une réponse que de son parent : la fenêtre principale
  répond par un relais chargé dans la fenêtre détachée
  (`displaySurfaceRelay.js`), dont le message part donc de celle-ci.

### 4. Déplacer une iframe recharge la page

Un navigateur détruit le contexte d'une iframe sortie de son document. Au
détachement comme au rattachement, le navigateur de l'OS réécrit la page à
partir du document déjà reçu, sans nouvelle requête au serveur (un `POST`
n'est pas renvoyé), avec les cookies et le `localStorage` actuels. L'état
JavaScript de la page est perdu, comme après un rechargement servi du cache.

### 5. Fin d'une surface

- L'utilisateur ferme la fenêtre détachée : état `ClosedByUser`, la page
  revient dans la fenêtre du navigateur de l'OS.
- La fenêtre quitte le document de la surface (l'utilisateur y tape une autre
  adresse) : état `Lost`, l'OS ferme la fenêtre et la page revient.
- Le propriétaire ferme la surface, ou se ferme : état `Closed`.
- Recharger ou fermer l'onglet de l'OS ferme toutes les surfaces ; la fermeture
  de session ferme les apps, donc leurs surfaces.

### 6. Fenêtre bloquée

Si le navigateur hôte refuse la fenêtre, le service publie lui-même la
notification de l'ADR 0078 (source `org.hackeros.display`, sans demander
`notifications.post` à l'app) et le navigateur de l'OS affiche un bandeau.

## Conséquences

- La fenêtre détachée dépend de deux détails internes de Blazor (délégation sur
  `document`, sélecteur `_bl_`). Le test E2E les surveille ; si une version de
  Blazor les change, l'initialiseur est le seul endroit à adapter.
- Les écouteurs posés sur `document` par d'autres bibliothèques (MudBlazor)
  sont aussi posés sur les surfaces : un clic dans une surface ferme, par
  exemple, un menu ouvert sur le bureau, comme le ferait un clic ailleurs sur
  le même écran.
- Une app peut détacher n'importe quelle partie de sa fenêtre avec
  `DisplaySurfaceContent`, sans capacité : la fenêtre n'ouvre aucun accès
  nouveau, elle montre ce que l'app montre déjà.
