# ADR 0061 : xterm.js comme émulateur du terminal

## Statut

Accepté le 2026-10-11.

- **Contexte du plan :** après la phase 4 (refonte terminée), demande de
  Benoit du 2026-10-11 : un rendu du terminal plus réaliste, avec la même
  librairie que la version legacy (`src/apps/terminal.ts`, xterm.js 5.3).
- **Ne remplace aucun ADR.** Les ADR 0050 (sortie affichée pendant que la
  commande tourne), 0051 section 4 (c'est le terminal qui interprète les
  séquences de `clear`) et 0058 section 4 (complétion par le shell, onglets)
  restent vrais ; seul le moyen de les réaliser change.

## Contexte

Jusqu'ici `TerminalPanel` affichait la sortie dans des `<div>` HTML, une par
ligne, et la ligne de commande dans un `<input>`. Conséquences :

- les couleurs ANSI étaient retirées (`TerminalControlSequenceDecoder` ne
  gardait que l'effacement de l'écran) ;
- pas de vrai curseur, pas de grille de caractères : une ligne trop longue se
  repliait comme du texte HTML, la sélection prenait les invites ;
- `nano` passait par un second rendu HTML (`TerminalScreen`), une `<span>` par
  caractère.

L'éditeur de code utilise déjà la même librairie que le legacy (Monaco, ADR
0020), embarquée par un bundle npm/esbuild versionné. Le terminal peut suivre
le même modèle.

## Décision

### 1. xterm.js, embarqué dans la librairie du terminal

`Shared/HackerOs.AppSdk.Terminal` embarque `@xterm/xterm` 6.0.0 et
`@xterm/addon-fit` 0.11.0, versions figées par `package-lock.json`.
`build.mjs` (esbuild) produit `wwwroot/xterm/xterm.bundle.js`,
`xterm.bundle.css` et `xterm.bundle.LICENSE.txt`, versionnés dans le dépôt :
le build .NET n'exécute pas npm. Une cible MSBuild fait échouer le build si
le bundle, le lockfile ou la licence manque (comme pour Monaco).

Le bundle n'est importé que par `TerminalPanel.razor.js`, le module propre au
composant ; aucun hôte ne référence de script (règle des hôtes de
`AGENTS.md`). Les apps (Terminal, CodeEditor, apps tierces) ne changent pas :
elles utilisent `TerminalPanel` ou `TerminalTabs` comme avant, sans
`IJSRuntime` (ADR 0055).

### 2. xterm.js joue le rôle du matériel, la discipline de ligne reste en C#

xterm.js est utilisé comme un terminal physique : il affiche les octets qu'on
lui écrit (séquences ECMA-48 comprises) et renvoie les octets du clavier
(`onData`). Tout le reste est en C#, dans la librairie :

| Rôle sous Linux | Ici |
|---|---|
| Écran et clavier du terminal | xterm.js (`TerminalPanel.xterm.entry.js`) |
| Décodage des touches (`0x7f`, `ESC [ A`…) | `TerminalInputDecoder` |
| Discipline de ligne (`ONLCR`, `^C`, file de frappe) | `TerminalPanel` |
| readline (édition, historique, complétion) | `TerminalLineEditor` + `IHackShell` |
| Programme plein écran (smcup, adressage du curseur, rmcup) | `TerminalAnsi.RenderFrame` |

Les sorties des commandes partent vers xterm.js sans autre transformation que
`\n` → `\r\n` (le drapeau `ONLCR` d'un tty). Les couleurs, `clear`
(`ESC[H ESC[2J ESC[3J`) et toute autre séquence sont donc interprétés par
l'émulateur, comme sous Linux.

### 3. Contrat inchangé pour les commandes plein écran

`IFullScreenTerminalSession` et `TerminalScreenFrame` ne changent pas. Le
panneau traduit chaque cadre en séquences : `ESC[?1049h` à l'entrée, une
ligne écrite en place par rangée, curseur positionné, `ESC[?1049l` à la
sortie (l'écran normal et le curseur reviennent comme avant la commande).
`TerminalScreen` reste dans la librairie (scénarios du BrowserHarness) mais
`TerminalPanel` ne l'utilise plus.

### 4. Raccourcis laissés au navigateur et à l'interface

Le gestionnaire de touches de xterm.js laisse passer : les raccourcis
d'onglets de `TerminalTabs` (Ctrl+Shift+T/W, Ctrl+PageUp/PageDown), Ctrl+V et
Ctrl+Shift+V (collage par l'événement `paste` du navigateur), Ctrl+C quand du
texte est sélectionné et Ctrl+Shift+C (copie). Sans sélection, Ctrl+C envoie
`^C` au terminal.

## Conséquences

- Couleurs ANSI, curseur clignotant, sélection et copie, repli des lignes à
  la largeur de la grille, défilement de 5000 lignes.
- Le bundle ajoute environ 340 Ko (non compressés) à la librairie du
  terminal. Il n'est chargé qu'à l'ouverture d'un terminal.
- Les tests unitaires couvrent le décodage, l'édition et le rendu (avec un
  petit écran virtuel de test) ; le rendu réel se vérifie dans un navigateur.
- `TerminalControlSequenceDecoder` reste une API publique de la librairie
  mais `TerminalPanel` ne s'en sert plus.
- Le lecteur d'écran ne reçoit plus une région `role="log"` : xterm.js a un
  mode lecteur d'écran (`screenReaderMode`) qui n'est pas activé. À
  reprendre avec le chantier accessibilité.
- Hors périmètre : une entrée standard pour les commandes (la frappe pendant
  une commande attend la prochaine invite), les caractères larges (CJK,
  emoji sur deux cellules).

Voir [`terminal-emulator.md`](../terminal-emulator.md).
