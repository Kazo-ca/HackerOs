> [English version](../../../../en/wasm/dev/adr/0054-shell-exit-status-and-detached-execution.md)

# ADR 0054 : `$?` par pipeline, shell détaché et écritures synchrones dans un tube

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 3 — Shell, commandes et corbeille
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.1).
- **Remplace en partie :** ADR 0050, section 3, pour un seul point : la valeur
  de `$?`. Le reste de l'ADR 0050 reste en vigueur.
- **Précise :** ADR 0050, sections 4 et 7, sur trois points que l'ADR ne
  tranchait pas.

## Contexte

En implémentant l'interpréteur (chantier `rebuild/3-shell-interpreter`), quatre
points se sont révélés absents ou contraires au comportement de bash :

1. L'ADR 0050 définit `$?` comme le code de « la dernière **liste** exécutée ».
   Sous bash, `$?` suit chaque **pipeline** : dans `false && …` puis
   `echo $?`, c'est le même résultat, mais dans `vrai && echo $?` bash affiche
   le code de `vrai`, pas celui de la ligne précédente.
2. L'ADR veut que les étapes tournent en même temps et qu'un écrivain attende
   quand le tube est plein, mais WebAssembly n'a qu'un fil : une écriture
   **synchrone** (`Write`, `WriteLine`) ne peut pas attendre.
3. L'ADR veut que `ExecuteCommandIntent` utilise le processus appelant comme
   processus du shell, mais la requête d'intention ne porte que l'identifiant
   de l'app appelante, pas son contexte.
4. `ExecuteCommandIntent` peut porter un jeton d'élévation à usage unique alors
   que la ligne peut lancer plusieurs commandes.

## Décision

1. **`$?` est mis à jour après chaque pipeline**, comme sous bash. Le code
   d'une ligne reste celui du dernier pipeline exécuté. Une ligne vide ne
   change pas `$?`.
2. **Une écriture synchrone dans un tube plein n'attend pas** : le tampon
   dépasse temporairement sa capacité. Seules les écritures asynchrones
   attendent de la place. Les lectures synchrones gardent la règle de
   l'ADR 0050 (exception sur un tube vide encore ouvert).
3. **`AppIntentDispatcher.DispatchAsync` accepte un contexte appelant
   facultatif** (`callerContext`). Fourni, son processus est celui du shell
   (parent des commandes, répertoire de départ) et sa passerelle de fichiers
   ouvre les redirections. Absent, la ligne tourne dans un **shell détaché** :
   commandes sans parent (environnement de connexion), répertoire
   `ExecuteCommandIntent.WorkingDirectory` ou dossier personnel, et toute
   redirection est refusée (`hsh: <cible>: cannot open files without a calling
   app`, code `1`).
4. **Le jeton d'élévation d'un `ExecuteCommandIntent` est consommé par la
   première commande lancée** de la ligne ; les suivantes tournent sans
   élévation.

## Conséquences

- `a && echo $?` affiche `0`, comme sous bash.
- Une commande qui écrit en synchrone beaucoup de données dans un tube consomme
  de la mémoire au lieu de bloquer ; toutes les commandes intégrées écrivent en
  asynchrone.
- Une app qui veut des redirections dans `ExecuteCommandIntent` passe son
  contexte. Le terminal n'utilise plus l'intention : il appelle directement
  `ShellInterpreter`.

## Références

- ADR 0042 (élévation), ADR 0050 (grammaire du shell), ADR 0051 (commandes).
- [`shell-interpreter.md`](../shell-interpreter.md).
