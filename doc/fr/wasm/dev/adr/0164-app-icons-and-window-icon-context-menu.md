# ADR 0164 : icônes des apps et menu contextuel de l'icône de fenêtre

## Statut

Accepté le 2026-10-22, à la demande de Benoit. Remplace les accès au menu par
clic gauche et clic droit sur toute la barre de titre de l'ADR 0095 §3.
Les raccourcis clavier de cette décision sont conservés.

## Contexte

Les apps système fenêtrées ne déclaraient aucun fichier dans `AppDefinition.Icons`.
Le chrome montrait un bouton de menu générique et la taskbar une icône générique.
Un chemin déclaré, relatif au paquet, était de plus utilisé directement comme URL
d'image, sans le préfixe de static web assets de sa bibliothèque.

## Décision

- La première icône de `presentation.iconAssetPaths` est celle de la fenêtre et
  de son bouton dans la taskbar. `WindowLaunchCoordinator` résout ce chemin par
  `StaticWebAssetAppAssetSource.RelativeUrl`, sous `_content/<assembly>/…`.
  Taskbar, sélecteur et autres projections réutilisent l'URL du même snapshot.
- Les quatorze apps système fenêtrées déclarent `icons/app.svg` et son asset image.
  Les SVG utilisent des glyphes Lucide déjà embarqués (licence ISC), dans une tuile
  colorée. Le générateur calcule leur SHA-256 ; aucun changement de schéma ni
  nouveau contrat SDK n'est nécessaire. Les apps tierces utilisent les mêmes
  propriétés `Icons` et `Assets`.
- Dans tous les thèmes de bureau sauf macOS, la barre de titre montre cette
  icône. macOS la masque par un jeton CSS interne de mise en page
  `--hos-window-icon-display`, mais son dock garde l'icône de l'app.
- Un clic gauche sur l'icône active seulement la fenêtre. Le menu des commandes
  de fenêtre s'ouvre au clic droit **sur l'icône**, et reste accessible par
  `Alt+Space` / `Alt+F3`. Le reste de la barre de titre n'ouvre plus ce menu.
- Sans icône déclarée, les composants gardent un symbole de fenêtre générique.
  Le menu utilise ce symbole, sans introduire de bouton hamburger.
- Les assets restent dans les bibliothèques des apps ; aucun hôte ni serveur
  supplémentaire requis. Le lanceur et les raccourcis gardent leur sélection
  Lucide existante, indépendante des images de la fenêtre.

## Vérification

- `WindowLaunchCoordinatorTests` : résolution du chemin d'icône et échappement URL.
- Génération des manifestes : assets présents et empreintes SHA-256 calculées.
- `WindowAppIconTests` (UI.E2E) : deux apps distinctes, images réellement décodées,
  même source dans chrome/taskbar, sept thèmes, exception macOS, clic gauche,
  clic droit sur le titre, clic droit sur l'icône et accès clavier.

## Tâches

- [x] Déclarer les icônes des apps système fenêtrées et résoudre leurs URLs.
- [x] Afficher l'icône dans chrome/taskbar et respecter l'exception macOS.
- [x] Réserver le menu au clic droit sur l'icône et conserver l'accès clavier.
- [x] Ajouter les tests de régression et mettre à jour les documents concernés.
