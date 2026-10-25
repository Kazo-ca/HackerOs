# ADR 0165 : accueil mobile, tiroir et aides souris

## Statut

Accepté le 2026-10-23 à la demande de Benoit. Complète le plan mobile archivé
pour le lanceur et l’accueil, tout en gardant les contrats Retour/Accueil/Récents.
Remplace l’exclusion des icônes mobiles de l’[ADR 0152 §7](0152-desktop-icons.md#7-mobile) :
l’accueil dispose désormais de raccourcis d’applications. Le mode Bureau de
l’explorateur sur `~/Desktop` reste réservé au desktop.

## Décision

- `MobileShell` affiche un accueil quand aucune fenêtre n’est au premier plan.
  Un dock précède la navigation système. Le cercle Accueil Android, la poignée
  centrale iOS ou le dock ouvrent le tiroir par glissement vers le haut ; un
  bouton du dock reste une alternative explicite.
- Le tiroir affiche les apps fenêtrées, visibles, activées et compatibles mobile.
  Les quatorze apps système fenêtrées déclarent le même composant comme point
  d’entrée desktop et mobile via `MobileEntryPoint` ; une app tierce desktop
  seulement n’est pas lancée en contournant son manifeste.
  Il permet recherche, lancement, ajout à une page et épinglage au dock. Toucher
  une app ouverte restaure son instance plutôt que créer une autre fenêtre.
- Les pages défilent au toucher, au stylet ou à la souris. L’édition s’ouvre par
  bouton, appui long ou clic droit. Elle permet réordonnancement, transfert entre
  pages, ajout/suppression de pages et retrait de raccourcis sans désinstallation.
- Le fichier VFS `~/.config/hackeros/.mobile/layout.json` est propre à l’utilisateur.
  Il contient `schemaVersion: 1`, `pages` (tableaux ordonnés d’IDs) et `dock`.
  C’est un dossier caché standard : l’explorateur masque les noms commençant
  par un point, avec une commande explicite d’affichage des fichiers cachés
  par fenêtre. Cette commande réutilise le contrat de filtrage du fournisseur.
  L’accueil mobile ne présente que des apps.
- Limites : 24 pages, 20 apps par page, 5 au dock. Aucun doublon entre pages ;
  une app peut être à la fois dans le dock et sur une page. Les IDs indisponibles
  sont conservés pour retrouver leur place après réactivation.
- Une action d’édition écrit un remplacement atomique du fichier. Les écritures
  sont sérialisées, jamais déclenchées à chaque mouvement. Les données invalides
  restent intactes à la lecture ; un échec de sauvegarde conserve la dernière projection valide.
- `mobile.config` est un document OS à couches enregistré dans le registre SDK.
  `mouseHelpers=true` par défaut montre flèches et conseils. Les masquer ne
  désactive ni les gestes souris ni les commandes accessibles du tiroir.
- La logique reste en C#. Le module RCL `wwwroot/mobileGestures.js` partage la
  capture Pointer Events, l’appui long et l’observation de taille. Il transmet
  un delta au relâchement et supprime le clic parasite d’un geste, en laissant
  passer un simple clic sur la poignée supérieure et les clics du volet.
- Les shells partagent `WindowProcessObserver` : les lancements par intent ou
  association de fichiers sont présentés aussi en mobile, et les fenêtres des
  processus arrêtés sont retirées. Aucune dépendance serveur ni nouveau contrat SDK.

## Vérification

Voir [mobile-home-screen.md](../mobile-home-screen.md) : format, architecture,
usage, tests de persistance/navigation et scénarios Android/souris, iOS/toucher.
