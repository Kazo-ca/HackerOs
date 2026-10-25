# ADR 0145 : `PublishAsync`, le vrai résultat d'une publication depuis un worker

## Statut

Accepté le 2026-10-18.

- **Réponse de Benoit :** option **B** de la carte « Comment traiter Publish
  d'un événement depuis une app qui tourne dans un worker ? » (2026-10-18,
  15:12Z, fil « Exec 1 : passerelles distantes »).
- **Remplace :** le paragraphe « Question ouverte » de l'ADR 0138. L'ADR 0138
  §3 reste vrai pour `Publish`.
- **Version :** ajout au SDK après la publication de la 1.2.0 (ADR 0132 §2,
  chantier `exec/10-final`) ; il reste dans `PublicAPI.Unshipped.txt` et part
  avec la prochaine mineure (1.3.0), selon SemVer
  ([`sdk-packages.md`](../../framework/sdk-packages.md)).

## Contexte

Dans un worker, le runtime .NET n'a qu'un thread et ne peut pas attendre l'OS
de façon synchrone. `IAppEventGateway.Publish(topic, payload)` est synchrone :
l'ADR 0138 §3 lui fait renvoyer un `Delivered` provisoire et écrire un refus de
l'OS dans le journal de l'app. Trois choix ont été proposés : garder ce
comportement seul (A), ajouter une forme asynchrone qui renvoie le vrai
résultat (B), ou refuser dans un worker les publications hors des sujets de
l'app (C). Benoit a choisi B.

## Décision

1. `IAppEventGateway` reçoit
   `Task<TopicPublishResult> PublishAsync<TPayload>(TopicName topic, TPayload payload, CancellationToken cancellationToken = default)`.
   Mêmes règles que `Publish` : identité du processus, sujet de l'app ou canal
   partagé autorisé.
2. Le membre a une implémentation par défaut qui appelle `Publish`, comme
   `IAppProcessGateway.KillAsync` (ADR 0072 §4) : une implémentation existante
   compile toujours, et une app 1.0, 1.1 ou 1.2 n'est pas touchée. Sur le fil
   principal, `PublishAsync` donne donc exactement le résultat de `Publish`.
3. Dans un worker, `PublishAsync` est un appel (`call`) de l'opération
   `ev.pubr`. L'OS publie au nom du pid (même chemin que `ev.pub`) et répond :
   - `o` : la valeur de `TopicPublishOutcome` ;
   - `f` : les fautes des abonnés, chacune avec l'encodage d'erreur du
     protocole (`RemoteErrors`). Le worker les recrée en
     `EventDispatchFault` avec le type de la charge.
4. Un refus rendu par `PublishAsync` n'est **pas** écrit dans le journal de
   l'app : c'est l'app qui lit le résultat. `Publish` garde le comportement
   de l'ADR 0138 §3 (provisoire, refus au journal).

## Conséquences

- Une app qui doit connaître le résultat réel appelle `PublishAsync`, partout.
  Le guide du SDK recommande cette forme pour le code qui peut tourner en
  worker.
- Nouvelle ligne dans `Shared/HackerOs.Simulation.Abstractions/PublicAPI.Unshipped.txt` ;
  `HackerOsVersion` reste `1.2.0` jusqu'à la prochaine publication.
- Test : `RemoteEventsTests.PublishAsync_returns_the_outcome_and_the_subscriber_faults_decided_by_the_OS`
  vérifie le même résultat sur le fil principal et en worker (livré, refusé,
  faute d'un abonné du fil principal), sans ligne de refus dans le journal.

## Références

- ADR 0072, 0124, 0132, 0138.
- [`worker-processes.md`](../worker-processes.md), [`app-sdk.md`](../../framework/app-sdk.md),
  [`sdk-packages.md`](../../framework/sdk-packages.md).
