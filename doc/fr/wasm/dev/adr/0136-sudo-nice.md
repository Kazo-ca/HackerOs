# ADR 0136 : `sudo nice` lance la commande en root

## Statut

Accepté le 2026-10-18 (réponse de Benoit « Oui, l'autoriser » à la carte
« Faut-il que `sudo nice -n -5 commande` marche ? », fil de l'ADR 0127).

- **Amende :** l'ADR 0051 §4 (`sudo` refuse toute commande qui déclare
  `shell.control`) pour la seule commande `nice`.
- **Complète :** l'ADR 0127 §1 (seul root baisse une valeur `nice`).

## Contexte

- Sous Linux, `sudo nice -n -5 make` est la façon habituelle de lancer une
  commande avec une priorité plus haute : `nice` fait un `exec` de la commande,
  qui hérite de l'identité root de `sudo`.
- Dans HackerOS, `nice` déclare `shell.control` : il lance sa commande par le
  shell (`IShellSession.RunNicedAsync`). L'ADR 0051 §4 fait refuser à `sudo`
  toute commande de ce genre (« shell builtin »), parce qu'un `cd` élevé
  n'aurait rien sur quoi agir. `sudo nice …` était donc refusé, et il fallait
  lancer la commande puis `sudo renice -n -5 -p PID`.
- Un ticket d'élévation (ADR 0042) ne sert qu'une fois : si `nice` lui-même
  était élevé, sa commande ne pourrait plus l'être.

## Décision

1. `sudo` accepte `nice` (`org.hackeros.commands.nice`), et lui seul, parmi les
   commandes qui déclarent `shell.control`. `sudo cd`, `sudo export` restent
   refusés comme avant.
2. Comme pour toute commande, `sudo` demande d'abord les identifiants d'un
   administrateur (une seule demande : « Run as root: nice -n -5 make »).
3. `nice` tourne sous l'identité de l'utilisateur et reçoit le ticket root de
   `sudo` avec son accès au shell. Il le passe à la commande qu'il lance, comme
   un `exec` : c'est la commande qui tourne en root. Elle est l'enfant de
   `nice`, lui-même enfant de `sudo`.
4. Avec ce ticket, `nice` peut baisser la valeur : la commande démarre à
   `nice + ajustement` (borné à -20..19), sans l'avertissement « Permission
   denied ».
5. Le ticket reste à usage unique : un second lancement par le même `nice` est
   refusé (`ElevationDenied`).

## Conséquences

- `sudo nice -n -5 make` marche comme sous Linux. `sudo renice` reste possible
  pour un processus déjà lancé.
- `ps` montre `nice` sous l'utilisateur et la commande sous root, alors que
  Linux n'a plus de processus `nice` après l'`exec`. C'est l'écart assumé du
  point 3 : il garde le ticket à usage unique.
- Aucune API publique ne change : le ticket passe par l'accès au shell interne
  (`ShellSessionAccess`).
