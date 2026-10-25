# ADR 0135 : aucun jeu sur ce repository

## Statut

Accepté le 2026-10-18 (Benoit, fil « ADR des suites de l'étape 5 », réponse
à la question E4).

- **Remplace :** l'[ADR 0092](0092-game-in-a-separate-repository.md) (qui
  remplaçait lui-même l'ADR 0023), et toute mention d'un jeu ou d'un
  « dépôt du jeu » dans les ADR acceptés avant celui-ci (0098, 0114, 0117,
  0132 et les autres) : ces mentions ne décident plus rien.

## Contexte

L'ADR 0092 a retiré le code de jeu de ce dépôt, mais les documents parlaient
encore d'un dépôt du jeu, de sa version du SDK et de ce qu'il ferait. Benoit,
le 2026-10-18 : « la version du jeu n'est pas de ce repository. On doit
l'éliminer des mémoires, de la documentation et le remplacer par une mention
**Aucun jeu sur ce repository** ».

## Décision

1. **Aucun jeu sur ce repository.** Ce dépôt construit la plateforme : l'OS
   simulé, son SDK, ses hôtes et ses apps de base.
2. La documentation ne décrit ni jeu, ni dépôt de jeu, ni ses versions ou ses
   besoins. Là où un document doit parler d'un consommateur de l'écosystème,
   il dit « un autre dépôt » ou « une app tierce ».
3. Les noms historiques restent tels quels quand ce sont des identifiants
   (branche `v3/2-game-removal`, nom de fichier de l'ADR 0092, ADR 0023) :
   on ne réécrit pas l'historique.
4. Les ADR acceptés ne sont pas modifiés (règle d'AGENTS.md) ; cet ADR les
   remplace sur ce point.

## Conséquences

- Documents courants nettoyés dans le même changement (`project-overview.md`,
  `building-on-hackeros.md`, `sdk-packages.md`, `ecosystem-rebuild-plan.md`,
  `wasm-v3-completion-plan.md`, `wasm-v3-final-status.md`,
  `execution-followups-plan.md`, documents des mesures, `README.md` de
  `wasm2/HackerOs` et index) ; quelques commentaires de code aussi.
- La mémoire du projet ne garde que la règle.

## Références

- ADR 0023, 0092.
