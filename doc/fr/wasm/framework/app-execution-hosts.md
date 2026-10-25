> [English version](../../../en/wasm/framework/app-execution-hosts.md)

# Hôte d'exécution des apps : fil principal ou Web Worker

Ce document décrit ce qu'une app déclare pour choisir où tourne son processus (fil principal ou Web Worker), ce qui traverse la frontière d'un worker et comment lire l'état d'isolation de la page. Le protocole, le pool, l'escalade, les limites de processus et les mesures sont dans [`dev/worker-processes.md`](../dev/worker-processes.md).

## Déclarer l'hôte

`[AppManifest(Execution = AppExecutionHost.…)]` déclare l'hôte ; le générateur
écrit le résultat dans `manifest.json` (`"execution"`).

| Genre | Défaut (`Default`) | Valeurs permises |
|---|---|---|
| Fenêtre, composant embarqué | `mainThread` | `mainThread` (validateur : `manifest.execution.window`) |
| Commande | `pool` | `pool`, `dedicated`, `mainThread` |
| Service | `dedicated` | `dedicated`, `pool`, `mainThread` |

Une fenêtre (composant embarqué compris) reste sur le fil principal : `mainThread` est la seule valeur permise pour elle.

## Par défaut

Côté app : rien, sinon déclarer l'hôte quand le défaut ne convient pas
(voir [`app-sdk.md`](app-sdk.md#processus-dans-des-web-workers-adr-0070-et-0071)).

## Ce qui traverse la frontière

Les types d'événements de l'OS qui ont un codec dans `WirePayloadCodec` (liste dans l'ADR 0138 §6) et les types d'une app déclarés par `[assembly: HackWireContext(typeof(MyJsonContext))]` traversent la frontière. Un autre type lève une `NotSupportedException` dans le worker, avec le nom de l'attribut à ajouter ; sur le fil principal, rien ne change.

Les appels de passerelle exécutés par l'OS pour un processus du worker gardent les mêmes vérifications de capacités qu'en local.

## Isolation cross-origin

Une app lit l'état sans capacité par `HackCrossOriginIsolation.GetStatusAsync()`
(SDK, espace `Diagnostics`, ADR 0146) : mêmes faits que les premières lignes de
`/proc/hackeros/workers`. Dans un worker, c'est l'appel asynchrone `coi.get`.
Diagnostic s'en sert pour son badge.

## Tâches `HackBackground`

Une tâche `HackBackground` lancée par une app (`HackBackground.RunAsync`) tourne dans un worker du pool quand l'hôte en a, ou sur le fil principal sans worker, avec la même sérialisation. Détails : [`dev/worker-processes.md`](../dev/worker-processes.md#tâches-hackbackground-adr-0128-adr-0140) et [`background-jobs.md`](background-jobs.md).

## Voir aussi

- [`app-sdk.md`](app-sdk.md#processus-dans-des-web-workers-adr-0070-et-0071) : résumé côté SDK.
- [`app-sdk.md`](app-sdk.md#isolation-cross-origin-adr-0146) : `HackCrossOriginIsolation`.
