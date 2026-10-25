> [English version](../../../../en/wasm/dev/adr/0152-desktop-icons.md)

# ADR 0152 : icônes du bureau

## Statut

Accepté le 2026-10-19.

- **Décision de Benoit :** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  validé le 2026-10-19, questions Q1 (`~/Desktop` plus Dossier perso et
  Corbeille), Q2 (grille et position libre mémorisée, réorganisation
  automatique), Q18 (portée `Desktop` du menu), Q20 (mobile : menu oui, icônes
  non), Q21 (fichiers `.desktop`).
- **S'appuie sur :** l'ADR 0148 (contrôle fourni par l'explorateur), l'ADR 0150
  (menu de l'OS), l'ADR 0151 (menus de fichiers et entrées inscrites).

## Contexte

`~/Desktop` est créé pour chaque utilisateur (`FileSystemSeeder`) et
`DesktopArea.BackgroundContent` attend un contenu, mais le bureau est vide.
Le contrôle de fichiers sait déjà lister, renommer, glisser-déposer ; il lui
manque une disposition en grille libre et la mémoire des positions.

## Décision

### 1. Le bureau affiche le contrôle de fichiers

`DesktopShell` remplit `DesktopArea.BackgroundContent` avec `HackFileView`
(ADR 0148) en **mode Bureau** (`FileViewMode.Desktop`) sur `~/Desktop`, avec
les droits de la session de l'utilisateur. Le bureau n'a pas son propre code
de liste : une correction de l'explorateur l'atteint aussi.

### 2. Mode Bureau

- Grille de cellules dont la taille suit la taille des icônes (petite,
  moyenne, grande), en colonnes de haut en bas puis de gauche à droite, comme
  Windows.
- Chaque icône occupe une case. Glisser une icône la déplace sur la case la
  plus proche (aimantée) ; une case occupée envoie l'icône à la case libre
  suivante.
- Sélection au clic, Ctrl+clic, Maj+clic et **lasso** ; renommage en place
  (F2 ou clic lent) ; double-clic ou Entrée ouvre ; Suppr met à la corbeille.
- Glisser-déposer avec l'explorateur et entre fenêtres (ADR 0037 §4), avec
  les mêmes règles de déplacement, copie et conflit que l'explorateur.
- Le fond du bureau (fond d'écran) reste celui du thème ; le mode Bureau est
  transparent.

### 3. Positions

`~/.config/hackeros/desktop.config` (format des réglages de l'OS,
[`settings-files.md`](../../framework/settings-files.md)) :

```ini
[positions]
notes.txt=0,2
Projets=1,0

[view]
auto-arrange=false
align-to-grid=true
icon-size=medium
show-home=true
show-trash=true
sort=name
```

- Un fichier nouveau prend la **première case libre** dans l'ordre de la
  grille.
- Un nom absent du dossier est **oublié au prochain enregistrement**.
- Renommer ou déplacer une icône réécrit sa ligne.
- **Réorganiser automatiquement** (`auto-arrange=true`) trie selon `sort`
  (nom, taille, type, date) et range en continu ; glisser une icône change
  alors l'ordre au lieu de la case.
- **Aligner sur la grille** ramène une icône hors grille (fenêtre redimensionnée)
  sur la case la plus proche.
- Quand l'écran rétrécit, une icône hors de l'écran est affichée sur la
  dernière case libre, sans changer sa position enregistrée.

### 4. Icônes système

- **Dossier perso** (`~`) et **Corbeille** sont des entrées **virtuelles**
  ajoutées par le bureau, pas des fichiers de `~/Desktop`. Leur place est
  dans `[positions]` sous les noms réservés `::home` et `::trash`.
- Affichées selon `show-home` et `show-trash` (Paramètres ▸ Bureau, et menu
  « Affichage » du bureau).
- Menu de Dossier perso : Ouvrir, Propriétés. Menu de la Corbeille : Ouvrir,
  Vider la corbeille (confirmation), Propriétés. L'icône de la Corbeille montre
  si elle est pleine ([`trash.md`](../trash.md)).
- Déposer un élément sur la Corbeille le met à la corbeille ; sur Dossier perso,
  le déplace dans `~`.

### 5. Raccourcis d'apps : fichiers `.desktop`

- `~/Desktop/<nom>.desktop`, format freedesktop « Desktop Entry » :
  `[Desktop Entry]`, `Type=Application`, `Name`, `Exec=<appId> [arguments]`,
  `Icon` (facultatif ; l'icône de l'app sinon).
- Créés en glissant une app du menu Démarrer sur le bureau, ou par
  « Envoyer vers ▸ Bureau (créer un raccourci) » dans le menu contextuel d'une
  app du menu Démarrer.
- Partout où le contrôle de fichiers les montre (bureau, explorateur,
  dialogues) : icône et nom de l'app, extension masquée ; double-clic = lancement
  de l'app par `LaunchApp` avec ses arguments, et ses propres droits.
- Un `.desktop` dont l'app est désinstallée ou désactivée s'affiche **grisé** ;
  l'ouvrir explique que l'app n'est pas installée.
- Un `.desktop` mal formé s'affiche comme un fichier texte.

### 6. Menu du bureau

La portée `Desktop` (ADR 0151 §1) est le menu du vide plus « Affichage ▸ »
(taille des icônes, Réorganiser automatiquement, Aligner sur la grille,
Afficher Dossier perso, Afficher Corbeille), « Trier par ▸ » et
« Arrière-plan… » (ouvre Paramètres ▸ Apparence). Les entrées inscrites avec
la portée `Desktop` s'y ajoutent. Les menus d'un fichier ou d'un dossier du
bureau sont les menus `File` et `Directory` habituels.

### 7. Mobile

Pas d'icônes sur le bureau : le lanceur mobile reste tel quel. Le menu de
l'OS (appui long) s'applique au reste (ADR 0150 §5).

### 8. Paramètres ▸ Bureau

Une page de Paramètres règle `[view]` : taille des icônes, réorganisation,
alignement, tri, icônes système affichées.

## Conséquences

- `~/Desktop` devient visible : les fichiers qu'une app y écrit apparaissent.
- [`settings-files.md`](../../framework/settings-files.md) gagne `desktop.config`.
- Le chantier 5 du plan livre le mode Bureau, les positions, les icônes
  système, les `.desktop`, le menu `Desktop` et la page de Paramètres.

## Alternatives écartées

- **Un composant de bureau séparé** : deux codes de liste à maintenir ; le
  contrôle de l'explorateur est la source de vérité (ADR 0148).
- **Grille sans position libre** : écartée par Benoit (Q2).
- **Raccourcis dans un format propre** : `.desktop` est le format connu de
  Linux et se lit au terminal.

## Références

- ADR 0037, 0148, 0150, 0151.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §5,
  [`trash.md`](../trash.md), [`settings-files.md`](../../framework/settings-files.md).
