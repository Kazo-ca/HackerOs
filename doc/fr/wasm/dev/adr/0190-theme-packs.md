> [English version](../../../../en/wasm/dev/adr/0190-theme-packs.md)

# ADR 0190 : packs de thèmes dans le Hack Store (v2)

## Statut

Accepté le 2026-10-29 par Benoit comme **plan de la v2 du Hack Store**
(fil « Planifier le Hack Store », réponses en fin de document). Rien n'est à
coder avant la v1 du Store ([ADR 0188](0188-hack-store-and-app-catalog.md)).
Complète l'[ADR 0041](0041-shared-theme-catalog-and-scoped-tokens.md) (thèmes
« données seulement ») et l'[ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
(fichiers `.theme`).

## Contexte

- Un thème est un fichier `.theme` de jetons vérifiés, sans CSS libre ni script
  (ADR 0103 §2.4), lu dans `/etc/hackeros/themes/` et `~/.config/hackeros/themes/`.
- L'OS n'a aujourd'hui **aucun** système de fonds d'écran en image choisis par
  l'utilisateur, de packs d'icônes, de sons ni de curseurs.
- Benoit veut des packs qui portent des thèmes, des fonds d'écran, des icônes,
  des sons et des curseurs.

## Décision

### 1. Le paquet de pack

Un **pack de thèmes** est une archive zip d'extension `.hacktheme` :

```text
hackeros-theme-pack.json   identité, version, nom, contenu, SHA-256 de chaque fichier
themes/*.theme             thèmes (ADR 0103)
wallpapers/*.png|*.jpg|*.webp
icons/                     pack d'icônes (PNG seulement, voir §3)
sounds/*.ogg|*.mp3         sons des évènements de l'OS
cursors/*.png              curseurs, avec leur point actif dans le JSON
preview.png                aperçu montré par le Store
```

Chaque partie est facultative, mais un pack en contient au moins une.

### 2. Publication et installation

- Un pack est publié dans le catalogue comme une app (ADR 0188 §4 à §6), avec
  `kind: "theme-pack"` dans `catalog.json` et un `store.json` qui pointe vers le
  dépôt du pack. La CI vérifie les formats, tailles et dimensions au lieu de
  compiler.
- Un pack ne contient **aucun code** : il s'installe **pour soi sans
  administrateur** depuis une source de confiance (règles de
  l'[ADR 0189](0189-app-sources-trust-and-per-user-install.md) §3), dans
  `~/.local/share/hackeros/theme-packs/<packId>/` ; pour le système, dans
  `/usr/share/hackeros/theme-packs/` avec la validation de l'ADR 0189 §4.
- Les catalogues de thèmes, fonds, icônes, sons et curseurs lisent aussi les
  dossiers des packs installés. Un identifiant de pack qui reprend un
  identifiant existant est refusé (pas de masquage, comme l'ADR 0103).
- Le Store a un onglet **Thèmes** à côté des apps.

### 3. Règles « données seulement »

- Pas de SVG dans un pack (un SVG peut contenir du script) : images en PNG,
  JPEG ou WebP seulement.
- Tailles maximales vérifiées à l'installation et par la CI (par exemple 5 Mo
  par fond d'écran, 1 Mo par son, 30 Mo par pack ; valeurs exactes fixées par
  le chantier).
- Aucune adresse distante dans les fichiers.

### 4. Systèmes de l'OS à créer avant

Chaque partie d'un pack demande un système de l'OS qui n'existe pas encore.
Chacun est un chantier à part, à faire avant que le Store accepte cette
partie :

1. Fonds d'écran en image choisis par l'utilisateur (Paramètres ▸ Apparence).
2. Packs d'icônes (remplacement des icônes d'apps et de fichiers, ADR 0026 et
   0164).
3. Sons des évènements de l'OS (notification, erreur, ouverture de session),
   avec un réglage pour les couper.
4. Thèmes de curseurs.

Les thèmes `.theme` existent déjà : un pack qui ne contient que des thèmes
peut être accepté dès que le Store gère les packs.

## Conséquences

- La v2 du Store réutilise tout le circuit de l'ADR 0188 (dossier relu, CI,
  branche `packages`) pour un contenu sans code.
- Quatre systèmes nouveaux à concevoir ; chacun aura sa propre ADR.

## Réponses de Benoit

- **Q10** B : packs avec thèmes, fonds d'écran, icônes, sons et curseurs.
- **Q29** A : le format complet est décrit ici ; chaque nouveau système de
  l'OS est un chantier à part, fait avant que le Store accepte cette partie.

## Tâches

- [ ] Format `.hacktheme` et vérifications (CI du catalogue et installation).
- [ ] Installation pour soi et pour le système, catalogues qui lisent les packs.
- [ ] Onglet Thèmes du Hack Store.
- [ ] Chantier : fonds d'écran en image.
- [x] Chantier : packs d'icônes. Fait par l'[ADR 0203](0203-icon-packs.md).
- [x] Chantier : sons des évènements de l'OS ([ADR 0204](0204-os-event-sounds.md)).
- [ ] Chantier : thèmes de curseurs.
