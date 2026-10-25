# ADR 0103 : images de Hack Paint dans les fichiers de l'OS et éditeur de thèmes (thèmes utilisateur en fichiers de jetons)

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/11-apps`).
Le §2.4 suit la réponse de Benoit du 2026-10-17 : un thème ne contient que des
jetons vérifiés.

- **Complète :** [ADR 0041](0041-shared-theme-catalog-and-scoped-tokens.md)
  (catalogue de thèmes, jetons CSS, thèmes « données seulement ») pour les
  thèmes créés par l'utilisateur, que l'ADR 0041 ne prévoyait pas ; ADR 0044
  (réglages en fichiers) ; ADR 0055 (apps sur le SDK public).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/11-apps` ; réponse 4 de Benoit (porter l'éditeur de thèmes, comme
  app système sur le SDK public).

## Contexte

- Hack Paint (`Apps/System/HackerOs.Apps.HackPaint`) déclare les capacités de
  lecture et d'écriture du dossier personnel et des dialogues de fichiers, mais
  n'a ni Ouvrir, ni Enregistrer, ni Exporter : son JS dessine des pixels RGBA sur
  un canvas.
- L'éditeur de thèmes de `src/` (`src/apps/theme-editor.ts`, 1 510 lignes, plus
  l'aide, la documentation et les aperçus) enregistre un JSON dans
  `/etc/themes/<id>.json` et injecte du CSS libre (`customCss`), que l'ADR 0041
  juge dangereux.
- Dans `wasm2`, on ne choisit qu'un thème du catalogue intégré, dans
  `/etc/hackeros/appearance.config` et `~/.config/hackeros/appearance.config`
  (clés `desktopThemeId`, `mobileThemeId`, `accent`, `animationsEnabled`).

## Décision

### 1. Hack Paint lit et écrit des images

- **Ouvrir** : PNG, JPEG, BMP, GIF (première image) et WebP, décodés par le
  navigateur (`createImageBitmap` dans le module JS du composant) en pixels RGBA
  remis au C#. Le fichier est lu par `HackFile` (C#) ; le JS ne touche jamais au
  système de fichiers.
- **Enregistrer / Enregistrer sous** : PNG par défaut, JPEG au choix (qualité
  réglable), encodés par le canvas (`toBlob`) puis écrits par `HackFile`. Les
  dialogues sont ceux du SDK (`OpenFileAsync`, `SaveFileAsync`).
- Taille maximale d'une image : 4 096 × 4 096 pixels, et 16 Mio par fichier
  (ADR 0094) ; au-delà, un message le dit avant de décoder.
- Titre de fenêtre `nom.png — Hack Paint`, avec `*` quand l'image est modifiée,
  et confirmation à la fermeture (ADR 0006) si elle n'est pas enregistrée.
- Hack Paint déclare `FileHandlers` pour ces types, actions `open` et `edit` ;
  il est le gestionnaire par défaut de `edit` (ADR 0096) pour les images.
  Il s'ouvre aussi par « Ouvrir avec » et par son nom avec un chemin en
  argument.

### 2. Éditeur de thèmes

#### 2.1 App

Nouvelle app système `org.hackeros.theme-editor`
(`Apps/System/HackerOs.Apps.ThemeEditor`), sur le SDK public seulement. Elle
reprend de `src/` : l'édition par groupes de jetons (couleurs, typographie,
espacements, fenêtres, barre des tâches, animations), l'aperçu en direct, l'aide
et la documentation des jetons ; elle ne reprend pas le CSS libre (§2.4).

#### 2.2 Fichier de thème

- Un thème est un fichier texte au format des réglages (ADR 0044), extension
  **`.theme`** (comme `index.theme` de freedesktop) :

  ```ini
  [theme]
  id = neon-crypt
  name = Neon Crypt
  base = hackeros
  platforms = desktop, mobile

  [tokens]
  --hos-bg-base = #0b0f0b
  --hos-accent = #39ff14
  --hos-font-mono = "JetBrains Mono", monospace
  ```

- `base` désigne un thème du catalogue : un jeton absent garde sa valeur.
- Emplacements, comme les thèmes d'un bureau Linux : `~/.config/hackeros/themes/`
  (l'utilisateur) et `/etc/hackeros/themes/` (l'administrateur, pour toute la
  machine). Le fichier est l'export : on le copie, on l'envoie, on l'ouvre.
- Le catalogue vu par Paramètres et par l'éditeur = thèmes intégrés + thèmes
  de `/etc` + thèmes de `~`. Un identifiant de `~` qui reprend un identifiant
  intégré ou de `/etc` est refusé (pas de masquage).
- `appearance.config` peut désigner un thème utilisateur par son `id`.

#### 2.3 Application des jetons

- `HackerOs.Theming.Abstractions` publie la **liste des jetons** avec leur type
  (couleur, longueur, durée, famille de polices, nombre) ; l'aide de l'éditeur
  est générée depuis cette liste.
- À la lecture, chaque valeur est vérifiée selon son type ; une ligne invalide
  ou un jeton inconnu est ignoré avec un avertissement (ADR 0044 §5).
- `ThemeScope` applique les jetons d'un thème utilisateur par
  `style.setProperty` sur son élément racine, via un petit module JS de
  `HackerOs.Theming.Blazor` (pas de `style="…"` dans le balisage, ADR 0007).

#### 2.4 Pas de CSS libre (choix de Benoit)

Un thème ne contient que des jetons vérifiés : ni CSS libre, ni police ou image
distante, ni script. C'est la règle « données seulement » de l'ADR 0041 §5
étendue aux thèmes utilisateur. Le `customCss` de `src/` n'est pas porté.

### 3. Code Editor

`v3/11` revérifie les points encore ouverts de l'ancienne liste
(`P4-W3-002`, `P4-W3-007` : assets hors ligne, gros fichiers, nettoyage des
workers) contre l'atelier de l'ADR 0062 et corrige ce qui manque ; aucune
décision nouvelle.

## Conséquences

- `v3/11` modifie Hack Paint, crée l'app Theme Editor, la liste des jetons, le
  chargement des thèmes en fichiers et leur application, avec des tests
  (ouverture et enregistrement d'une image, thème invalide ignoré, thème
  utilisateur appliqué après rechargement) et des scans axe des deux apps
  (`v3/13`).
- Paramètres liste les thèmes utilisateur et ouvre l'éditeur.
- Nouvelle doc `theme-editor.md`, mises à jour de `project-overview.md`,
  `settings-files.md` et `sdk-reference.md` (liste des jetons).
