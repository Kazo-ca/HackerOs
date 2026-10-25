# API du terminal pour les apps et les commandes

Ce document décrit ce qu'une app ou une commande voit du terminal : le composant `TerminalPanel`, l'interface `IHackTerminal`, la lecture d'un mot de passe sans écho et le mode plein écran. Le fonctionnement interne (xterm.js, discipline de ligne, rendu, raccourcis) est dans [`dev/terminal-emulator.md`](../dev/terminal-emulator.md).

## Composant `TerminalPanel`

`TerminalPanel` implémente `IHackTerminal` : un hôte peut y lancer des
commandes (affichées à l'invite, après les touches déjà tapées), changer de
dossier par un `cd` visible et lire le texte affiché sans séquences de
contrôle (`TerminalAnsi.StripControlSequences`).

`TerminalPanel.PushVirtualKey` (clavier virtuel) suit le même chemin que le
clavier physique ; avec `UseVirtualKeyboard`, la zone de saisie cachée de
xterm.js reçoit `inputmode="none"`.

## Entrée standard d'une commande

Pendant qu'une ligne s'exécute, les touches vont à l'entrée standard de la commande, comme un tty en mode canonique : `read`, `cat` et `sh` interactif lisent les lignes envoyées par Entrée, Ctrl+D sur une ligne vide signale la fin d'entrée, Ctrl+C interrompt. Ce que la commande n'a pas lu revient à l'invite suivante, dans la limite de 4096 touches. Le détail de la discipline de ligne est dans [`dev/terminal-emulator.md`](../dev/terminal-emulator.md).

## Lire un mot de passe sans écho

Une commande peut couper l'écho, comme le drapeau `ECHO` de `termios` (ADR
0120 §4) : `TerminalInputReader` implémente `ITerminalInputMode`, et
`TerminalExecutionContext.ReadSecretLineAsync(invite)` met `EchoEnabled` à
`false` le temps d'une ligne, comme `getpass(3)`. La discipline de ligne
(`HandleInputKey`) n'affiche alors ni les caractères ni leur effacement
(Retour arrière, Ctrl+U) ; Entrée passe toujours à la ligne. `passwd` lit
ainsi les mots de passe.

## Commandes plein écran

Une commande plein écran utilise le contrat `IFullScreenTerminalSession` du SDK : `EnterAlternateScreenAsync` passe sur l'écran alternatif, `RenderAsync` reçoit un `TerminalScreenFrame` (lignes, curseur, libellé accessible) à chaque image, et `ReadKeyAsync` attend la prochaine touche ou le prochain redimensionnement. Une rangée peut porter des séquences de couleur ou de vidéo inverse (SGR) ; elle se termine par `ESC[0m`. Le rendu côté terminal est dans [`dev/terminal-emulator.md`](../dev/terminal-emulator.md#commandes-plein-écran).

## IHackTerminal

`IHackTerminal` (fichier `Shared/HackerOs.AppSdk/Components/IHackTerminal.cs`) a pour membres `RunCommandAsync(commandLine)`, `SetWorkingDirectoryAsync(path)`, `SendInputAsync(text)`, `ReadOutput(maxCharacters)` (65 536 caractères par défaut), `ClearAsync()` et `FocusAsync()`.

Les commandes envoyées par un hôte (`IHackTerminal.RunCommandAsync`) ne sont
jamais continuées.

