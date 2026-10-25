> [English version](../../../../en/wasm/dev/adr/0124-remote-event-and-watch-gateways.md)

# ADR 0124 : passerelles distantes d'événements et de surveillance de fichiers, pool ouvert à toute assembly d'app

## Statut

Accepté le 2026-10-18.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décision 1 ; écrit par le chantier `exec/0-adr` (branche `exec/1-adr`), mis
  en œuvre par `exec/1-remote-gateways`.
- **Remplace :** l'ADR 0072 §3 pour `HackEvents` et `Watch` (le reste de ce
  paragraphe, hôte de shell et administration, est inchangé).
- **Complète :** l'ADR 0070 §4 (chargement des assemblies dans un worker),
  l'ADR 0080 §1 (`hackwebd` sur le fil principal, qui devient sans objet).

## Contexte

Depuis l'ADR 0072, les phases web et v3 ont ajouté des passerelles distantes
au contexte d'exécution d'un worker (`RemoteAppExecutionContext`) : catalogue
et applications par défaut, intents, contrôle et notification des services,
presse-papiers, notifications, ports à l'écoute, base d'un site. Il reste, en
2026-10-18 sur `v3.1/base` :

| Membre | Comportement dans un worker |
|---|---|
| `IAppEventGateway` (6 membres : événements typés et sujets) | `NotSupportedException` (`RemoteEventGateway`) |
| `IAppFileSystemWatchGateway.WatchAsync` | `NotSupportedException` (`RemoteWatchGateway`) |
| `IAppProcessGateway.StartChild`, `Kill` et `ListProcesses` synchrones | `NotSupportedException` ; les formes asynchrones existent (`KillAsync`, `GetProcessesAsync`) |
| `IAppFileSystemGateway.WithSelectedHandle` | `NotSupportedException` |

Deux apps restent en `MainThread` pour cette seule raison : le service
d'exemple (`ServiceSampleApp`, sujet typé `SampleTickerEvent`) et `hackwebd`
(`HackFileSystemWatcher` sur ses dossiers de configuration, ADR 0080 §1).

Par ailleurs, le pool précharge `HackerOs.Commands.dll` et charge les autres
assemblies par `INTERNAL.loadLazyAssembly` au premier `start` ; une commande
d'une autre assembly (un projet d'exemple, une app empaquetée par NuGet dans un
hôte) n'a jamais été testée en worker. L'installation d'apps **au runtime**
reste différée (ADR 0094) : toutes les assemblies d'apps sont connues au build
(`BuildKnownLazyAssemblies`).

## Décision

### 1. Événements entre un worker et l'OS

Le bus d'événements reste dans l'OS, sur le fil principal. Un worker y accède
par messages, comme un client X11 parle au serveur :

- **Abonnement** : `Subscribe` et `SubscribeChannel` envoient l'opération
  `ev.sub` (sujet ou type, capacité du canal). L'OS crée l'abonnement **au nom
  du pid**, avec ses droits, et pousse chaque message par un message `ev`
  (identifiant d'abonnement, charge sérialisée). Le worker livre les messages
  dans l'ordre, dans le contexte ambiant du processus (comme `DeliverIntent`).
- **Désabonnement** : `Dispose` envoie `ev.unsub` ; la fin du processus, la
  mort de son worker et `SIGKILL` désabonnent côté OS sans attendre le worker.
- **Publication** : `Publish` envoie `ev.pub` et attend le
  `TopicPublishResult` (ou la liste des `EventDispatchFault`) de l'OS.
  `RegisterSharedChannel` passe par `ev.chan` avec la même politique qu'un
  appel local.
- **Pression** : un canal borné garde sa capacité côté OS ; quand il déborde,
  la règle du canal s'applique (comme sur le fil principal). Un abonnement par
  callback a un crédit de 256 messages en vol ; au-delà, l'OS attend
  l'accusé du worker, comme pour les flux (crédit de 64 Kio, ADR 0072 §2).

### 2. Ce qui traverse : charges sérialisables déclarées

Un objet du tas de l'OS ne peut pas être remis à un autre runtime. Une charge
traverse si son type est **sérialisable par System.Text.Json avec des
métadonnées générées** (compatible avec le trimming, ADR 0113) :

- les types d'événements du SDK et des contrats de l'OS (`FileSystemChangeEvent`
  et les autres événements publics) reçoivent leur codec dans le protocole des
  workers (`WireCodec`, sans réflexion) ;
- un type défini par l'app est déclaré par un `JsonSerializerContext` de son
  assembly, nommé par un nouvel attribut du SDK :
  `[assembly: HackWireContext(typeof(MyAppJsonContext))]`. Le même attribut
  sert aux tâches de l'ADR 0128.

Un type qui n'est ni l'un ni l'autre lève, **dans le worker**, une
`NotSupportedException` dont le message nomme le type et l'attribut à ajouter.
Sur le fil principal, rien ne change : les objets restent passés par référence.

### 3. Surveillance de fichiers

`WatchAsync` envoie `watch.open` (chemin, portée). L'OS ouvre la surveillance
au nom du pid, avec ses droits de lecture, et pousse les
`FileSystemChangeEvent` par le canal de §1. Fermeture, mort du processus et
débordement suivent §1.

### 4. Pool ouvert à toute assembly connue au build

- Le pool charge à la demande **toute** assembly de `BuildKnownLazyAssemblies`
  (point d'entrée validé par la découverte de l'OS, ADR 0113), et la garde
  chargée pour les demandes suivantes de ce worker.
- `PreloadAssemblies` reste un réglage d'hôte ; il ne contient que
  `HackerOs.Commands.dll` par défaut.
- Une assembly qui ne fait pas partie de la liste est refusée (`TypeLoadException`
  remontée comme échec de lancement, code de sortie 127). L'installation au
  runtime ne change pas cette règle tant que son propre ADR n'existe pas.

### 5. Ce qui reste refusé dans un worker

`StartChild` et les formes synchrones de `Kill` et `ListProcesses` restent
refusés (leurs formes asynchrones suffisent) ; `WithSelectedHandle`, l'hôte de
shell et l'administration aussi. Une app qui en a besoin garde `MainThread`
avec la raison en commentaire (ADR 0070 §1).

### 6. Migration

- `ServiceSampleApp` et `hackwebd` passent en worker dédié (genre `Service`,
  ADR 0070) ; leur commentaire de raison disparaît.
- Chaque autre app en `MainThread` est revue : la raison reste écrite si elle
  tient toujours.

## Conséquences

- Nouvelles opérations du protocole (`ev.sub`, `ev.unsub`, `ev.pub`, `ev.chan`,
  `watch.open`, message `ev`) documentées dans
  [`worker-processes.md`](../worker-processes.md).
- Nouvelle API publique `HackWireContextAttribute` (SDK 1.2, ADR 0132).
- Tests de parité : chaque scénario de `HackEvents` et de `Watch` tourne sur le
  fil principal et dans la boucle locale des workers (`LoopbackWebWorkerRuntime`)
  avec le même résultat ; une commande du projet d'exemple tiers tourne en
  worker dans les tests E2E.
- Une charge non déclarée échoue en worker et marche sur le fil principal :
  c'est voulu (le message d'erreur le dit), et le guide du SDK l'explique.

## Références

- ADR 0070, 0072, 0080, 0094, 0113, 0128, 0132.
- [`worker-processes.md`](../worker-processes.md),
  [`hack-file-api.md`](../../framework/hack-file-api.md).
