> [English version](../../../en/wasm/dev/terminal-emulator.md)

# Émulateur du terminal (xterm.js)

Le contrat côté apps et commandes (`TerminalPanel`, `IHackTerminal`, lecture d'un mot de passe sans écho, mode plein écran) est dans [`terminal-api.md`](../framework/terminal-api.md).

## Objet

`TerminalPanel` (`Shared/HackerOs.AppSdk.Terminal`) affiche son shell dans un
vrai terminal à grille de caractères, xterm.js, la librairie de la version
legacy (ADR 0061). Les commandes peuvent écrire des couleurs ANSI, `nano`
utilise l'écran alternatif, et le rendu ressemble à un terminal Linux.

La console (`Apps/System/HackerOs.Apps.Terminal`) utilise `TerminalTabs`.
L'éditeur de code n'utilise plus `TerminalPanel` directement : il embarque le
terminal que l'app Terminal enregistre pour le contrat `hackeros.terminal`
(`TerminalEmbed`, ADR 0063, [`embedded-components.md`](../framework/embedded-components.md)).

## Architecture

```text
clavier ─▶ xterm.js ──onData("\x1b[A")──▶ TerminalPanel.OnTerminalData
                                          └─ TerminalInputDecoder → TerminalKeyEvent
                                              ├─ commande plein écran active → TerminalFullScreenSession
                                              ├─ Ctrl+C pendant une commande → annulation, « ^C »
                                              └─ file de frappe → TerminalLineEditor / IHackShell
commande ─▶ TerminalOutputWriter ─▶ TerminalPanel.Write ("\n" → "\r\n") ─▶ xterm.js.write
```

| Fichier | Rôle |
|---|---|
| `TerminalPanel.xterm.entry.js` | Point d'entrée du bundle : crée le `Terminal`, le `FitAddon`, le thème (jetons `--hos-*`), le gestionnaire de touches (copie, collage, raccourcis d'onglets) et l'observateur de taille. |
| `build.mjs`, `package.json`, `package-lock.json` | Construisent `wwwroot/xterm/xterm.bundle.{js,css,LICENSE.txt}`. |
| `TerminalPanel.razor.js` | Module du composant : `mountTerminal(host, dotNet, options)` renvoie `write`, `focus`, `dimensions`, `dispose`. |
| `TerminalPanel.razor` | Discipline de ligne : routage des touches, exécution des lignes, invite, sorties, écran alternatif. |
| `TerminalInputDecoder.cs` | Octets du clavier → `TerminalKeyEvent` (flèches, `ESC[3~`, Ctrl+lettre, Alt, collage multi-lignes). |
| `TerminalLineEditor.cs` | Ligne en cours d'édition et séquences qui la redessinent, même repliée sur plusieurs rangées. |
| `TerminalAnsi.cs` | Invite colorée, `ONLCR`, suivi « curseur en début de ligne », rendu d'un `TerminalScreenFrame`. |

### Thème

Le fond, le texte et le curseur viennent des jetons de design lus sur
l'élément hôte (`--hos-bg-base`, `--hos-text-primary`, `--hos-accent`) ; la
police vient de `--hos-font-mono` (`0.875rem`, 14 px à 100 %). La palette des 16 couleurs ANSI est
celle du legacy, avec le vert d'accent de HackerOS. Le style du cadre est dans
`TerminalPanel.razor.css` ; la grille elle-même est dessinée par xterm.js.

Accessibilité ([`accessibility.md`](accessibility.md), ADR 0105) : `TerminalPanel` lit
`AppContext.Accessibility` à l'ouverture et suit `Changed`. La taille de police est relue du
panneau, dont la taille en `rem` suit la taille du texte ; en contraste élevé, xterm.js reçoit une
palette dont chaque couleur atteint 7:1 sur fond noir, un curseur et une sélection jaunes ; en
mouvement réduit, le curseur ne clignote pas. `applyAccessibility` (objet renvoyé par
`mountTerminal`) applique un changement et réajuste la grille.

## Utilisation au clavier

| Touche | Effet |
|---|---|
| ← → Début Fin, Ctrl+A/E/B/F | Déplacent le curseur dans la ligne. |
| Retour arrière, Suppr, Ctrl+H | Effacent un caractère. |
| Ctrl+W, Alt+Retour arrière | Effacent le mot avant le curseur. |
| Ctrl+U / Ctrl+K | Effacent jusqu'au début / jusqu'à la fin. |
| ↑ ↓, Ctrl+P/N | Historique du shell. |
| Tab | Complétion par le shell ; un second Tab liste les candidats sous la ligne, puis l'invite revient avec la ligne ([`terminal-console.md`](terminal-console.md)). |
| Ctrl+C | Avec une sélection : copie. Sinon, pendant une commande : l'annule (`^C`). À l'invite : abandonne la ligne. |
| Ctrl+Shift+C / Ctrl+V / Ctrl+Shift+V | Copie / collage. Un collage de plusieurs lignes exécute chaque ligne. |
| Ctrl+L | Efface l'écran et garde la ligne en cours. |
| Ctrl+D | Ligne vide : `exit`. Sinon : efface le caractère sous le curseur. |

La copie lève un vrai événement `copy` (`document.execCommand("copy")`) au lieu
d'écrire `navigator.clipboard` : le pont du presse-papiers de l'OS la reçoit
comme une copie native (origine `GuestNative`) et ne l'envoie à la vraie machine
que si `/etc/hackeros/clipboard.config` le permet ([`clipboard.md`](clipboard.md),
ADR 0065).

Chrome se réserve Ctrl+W, Ctrl+N et Ctrl+T (fermer l'onglet, nouvelle
fenêtre, nouvel onglet) tant que HackerOS n'est pas installé en PWA : ces
touches n'atteignent alors pas le terminal.

Pendant qu'une ligne s'exécute, les touches vont à l'**entrée standard** de
ses commandes (ADR 0064 §6, `TerminalInputReader`), comme un tty en mode
canonique : les caractères sont affichés en écho, Retour arrière et Ctrl+U
effacent, Entrée envoie la ligne (`read`, `cat`, `sh` interactif la lisent),
Ctrl+D sur une ligne vide est une fin d'entrée lue une fois, Ctrl+C
interrompt. Ce que la commande n'a pas lu revient à l'invite suivante comme
frappe anticipée (4096 touches au plus) : `sleep 5` puis `ls` + Entrée tapé
pendant l'attente lance `ls` ensuite, comme sous Linux.

Une commande inachevée (`for f in *; do` + Entrée, guillemet ouvert, `|`
final, here-document) affiche l'invite de continuation `>` de bash (`PS2`) :
le terminal le demande au shell par `IHackShell.NeedsMoreInput`, accumule les
lignes, puis les exécute ensemble ; Ctrl+C abandonne. L'historique garde une
seule ligne (`TerminalHistory.ToSingleLine` : `for f in *; do echo $f; done`).

## Commandes plein écran

Le contrat `IFullScreenTerminalSession` ne change pas. À l'entrée, le panneau
écrit `ESC[?1049h` ; chaque `TerminalScreenFrame` est dessiné rangée par
rangée (coupé à la largeur, sans repli), puis le curseur est placé ou caché.
Depuis l'ADR 0064, une rangée peut contenir des séquences SGR (couleurs,
vidéo inverse : `htop`, barres de `nano`) : `TerminalAnsi.FitToColumns` les
garde entières sans les compter dans la largeur, et un `ESC[0m` termine la
rangée ;
à la sortie, `ESC[?1049l` rend l'écran normal tel qu'il était. La taille de
la grille (`UpdateTerminalViewport`) est donnée à la session dès le début de
chaque commande, et un redimensionnement envoie `TerminalKey.Resize`.

## Décisions

- **xterm.js n'est qu'un écran et un clavier.** L'édition de ligne, la
  complétion et l'historique restent en C# : le shell et son contrat
  (`IHackShell`) ne changent pas, et le comportement se teste sans navigateur.
- **Bundle versionné**, comme Monaco : pas de npm au build .NET.
  `package.json` et `package-lock.json` sont retirés du contenu publié (sinon
  ils entrent en collision avec ceux du CodeEditor dans la publication de
  l'hôte Ecosystem).
- **Sortie d'erreur sans couleur**, comme un vrai terminal ; seuls les
  messages du terminal lui-même (échec du démarrage du shell, `Fault:`) sont
  en rouge et les bannières d'approbation en jaune.
- **Invite colorée comme bash sous Debian** : `user@hôte` en vert gras,
  le dossier en bleu gras.
- **`nano` suit GNU nano** (ADR 0064 §7) : `^O` demande le nom du fichier
  (pré-rempli) et sert aussi d'« enregistrer sous », car xterm.js envoie le
  même octet `0x0F` pour Ctrl+O et Ctrl+Maj+O ; `^S`, `^W`/`^F`, `^K`/`^U`,
  `^C`, `^G`, `^A`/`^E`, `^Y`/`^V` ; barre de titre et deux rangées de
  raccourcis en vidéo inverse ; la ligne du curseur défile horizontalement
  (`<` et `>` aux bords) ; tabulations sur 8 colonnes. Ctrl+W reste réservé
  par Chrome hors PWA : `^F` cherche aussi.

## Mettre à jour xterm.js

```bash
cd wasm2/HackerOs/Shared/HackerOs.AppSdk.Terminal
# modifier les versions dans package.json
npm install && npm run build
```

Puis versionner `package-lock.json` et `wwwroot/xterm/`.

## Tests

- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalInputDecoderTests.cs`
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalLineEditorTests.cs`
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalAnsiTests.cs`
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalInputReaderTests.cs` (entrée standard)
- `Tests/HackerOs.Commands.Nano.Tests/NanoCommandTests.cs` (Write Out pré-rempli, couper/coller, recherche, défilement horizontal)
- `VirtualScreen.cs` (dans le même projet) : petit écran à grille de
  caractères qui applique les séquences écrites, pour vérifier où finissent le
  texte et le curseur (repli, retour au début du bloc de l'invite, cadres).

Vérifié dans Chromium sur une publication Release : commandes, repli d'une
longue ligne, édition au milieu, historique, complétion et liste au second
Tab, Ctrl+C, Ctrl+L, nouvel onglet et `exit`, `nano` (écran alternatif puis
retour), terminal intégré de l'éditeur de code.

## Liste des tâches

- [x] Embarquer xterm.js 6.0 et le fit addon (npm/esbuild, bundle versionné, licence, cible MSBuild).
- [x] Monter le terminal depuis le module du composant, thème depuis les jetons de design.
- [x] Décoder les octets du clavier en `TerminalKeyEvent` (`TerminalInputDecoder`).
- [x] Édition de ligne façon readline et redessin sur plusieurs rangées (`TerminalLineEditor`).
- [x] Sorties des commandes vers xterm.js avec `ONLCR`, invite colorée.
- [x] Écran alternatif pour les commandes plein écran.
- [x] Copie, collage, raccourcis d'onglets laissés à l'interface.
- [x] Clavier virtuel par le même chemin.
- [x] Tests unitaires, vérification dans un navigateur.
- [x] Entrée standard des commandes en mode canonique (`TerminalInputReader`, ADR 0064).
- [x] Couleurs dans les cadres plein écran ; `nano` aux raccourcis de GNU nano (ADR 0064).
- [x] Écho coupé pour les mots de passe (`ITerminalInputMode`, `ReadSecretLineAsync`, ADR 0120).
- [x] ADR 0061, cette page, index.
