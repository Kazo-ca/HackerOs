# ADR 0105 : préférences d'accessibilité en fichiers (contraste élevé, taille du texte, mouvement réduit) et tests automatisés

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/13-accessibility`).

- **Complète :** [ADR 0041](0041-shared-theme-catalog-and-scoped-tokens.md)
  (jetons de thème, attribut `data-motion` de `ThemeScope`), ADR 0044 (réglages
  en fichiers), [ADR 0095](0095-window-events-sizes-and-keyboard.md) (commandes
  clavier des fenêtres).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/13-accessibility` ; manque R11. La checklist manuelle
  d'accessibilité est écartée par Benoit : seuls des tests automatisés.

## Contexte

- Le mouvement réduit n'est géré qu'en CSS (`prefers-reduced-motion` dans
  `themes.css` et une dizaine de feuilles de composants) et par le réglage
  `animationsEnabled` de `appearance.config`. Rien pour le contraste ni la
  taille du texte ; aucune API du SDK.
- Les scans axe (`AccessibilityAndVisualCoverageTests`) couvrent l'explorateur,
  Paramètres, le Code Editor, Hack Paint et le bureau, pas le Terminal, le
  navigateur, la calculatrice ni le moniteur système. Aucun parcours au clavier
  n'est testé.

## Décision

### 1. Préférences

- Fichier `accessibility.config`, en couches comme tout réglage :
  `/etc/hackeros/accessibility.config` (défaut machine) et
  `~/.config/hackeros/accessibility.config` (utilisateur). Clés :

  | Clé | Valeurs | Défaut |
  |---|---|---|
  | `highContrast` | `auto`, `on`, `off` | `auto` (suit `prefers-contrast: more` et `forced-colors: active` de l'hôte) |
  | `textScale` | `1.0`, `1.25`, `1.5`, `2.0` | `1.0` |
  | `reducedMotion` | `auto`, `on`, `off` | `auto` (suit `prefers-reduced-motion` de l'hôte) |

- `animationsEnabled` de `appearance.config` reste lu : `false` équivaut à
  `reducedMotion = on`. Paramètres n'affiche plus qu'un réglage, « Réduire les
  animations », dans une nouvelle page **Accessibilité** avec les deux autres.
- L'écran de connexion utilise les préférences de `/etc` (aucun utilisateur
  n'est encore connu).

### 2. Application par le thème

- `ThemeScope` ajoute `data-contrast="high"` et la variable
  `--hos-text-scale` à côté de `data-motion`.
- `themes.css` gagne une couche de contraste élevé qui redéfinit les jetons de
  couleur, de bordure et de focus pour **tous** les thèmes (contraste du texte
  d'au moins 7:1, indicateur de focus de 3 px), et les tailles de texte de la
  plateforme passent en `rem` multipliés par `--hos-text-scale`.
- Les thèmes utilisateur de l'ADR 0103 ne peuvent pas désactiver la couche de
  contraste élevé : elle s'applique par-dessus.

### 3. SDK

- `HackAccessibility.GetPreferencesAsync()` renvoie les préférences effectives
  (après `auto`) et `HackAccessibility.PreferencesChanged` signale un changement.
- Une app qui dessine elle-même (canvas de Hack Paint, xterm.js du Terminal,
  Monaco du Code Editor) les applique : police du terminal et de l'éditeur
  agrandie, thème de contraste élevé de Monaco et de xterm, animations coupées.

### 4. Tests automatisés

- Scans axe ajoutés pour le Terminal, le navigateur, la calculatrice, le
  moniteur système, Theme Editor, la page de sauvegarde de Paramètres et la page
  Accessibilité ; chaque scan tourne aussi avec `highContrast = on`. Une
  violation sérieuse ou critique fait échouer le test (règle actuelle).
- Un test E2E de parcours au clavier, sans souris : ouvrir le lanceur, lancer
  une app, passer d'une fenêtre à l'autre (Alt+`), déplacer une fenêtre
  (Alt+F7), fermer (Alt+Q), avec un focus toujours visible.
- Un test vérifie que `textScale = 2.0` ne fait pas déborder le bureau ni les
  fenêtres des apps système (règle de débordement existante).

## Conséquences

- `v3/13` ajoute le document de réglages, la page Accessibilité, la couche de
  contraste élevé, `HackAccessibility`, l'adaptation des trois apps qui dessinent
  elles-mêmes et les tests.
- `settings-files.md`, `sdk-reference.md`, `building-and-testing.md` (tests axe)
  et `terminal-emulator.md` sont mis à jour.
