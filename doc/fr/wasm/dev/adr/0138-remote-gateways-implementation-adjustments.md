> [English version](../../../../en/wasm/dev/adr/0138-remote-gateways-implementation-adjustments.md)

# ADR 0138 : ajustements de la mise en œuvre de l'ADR 0124 (passerelles distantes)

## Statut

Accepté le 2026-10-18.

- **Chantier :** `exec/1-remote-gateways` (plan
  [`execution-followups-plan.md`](../plans/execution-followups-plan.md), décision 1).
- **Complète :** l'ADR 0124, sans en changer la décision. Elle consigne ce que
  le code a dû préciser ou faire autrement, et pourquoi.

## Contexte

L'ADR 0124 fait passer `HackEvents` et `Watch` d'un worker vers l'OS par
messages, ouvre le pool à toute assembly connue au build, et sort le service
d'exemple et `hackwebd` du fil principal. Trois contraintes ont pesé sur la
mise en œuvre :

1. Le runtime d'un worker n'a qu'un thread : il ne peut pas attendre de façon
   synchrone la réponse de l'OS (il ne recevrait jamais le message qui la porte).
2. Le contrat `IAppEventGateway` du SDK 1.0 est synchrone : `Publish` et
   `RegisterSharedChannel` renvoient leur résultat tout de suite.
3. Les bus de l'OS distribuent strictement par type CLR ; un type d'une app
   n'existe pas forcément dans le runtime de l'OS sous la même identité.

## Décision

### 1. Bus « wire » côté OS

`InMemoryTopicMessageBus` et `InMemoryEventBus` implémentent deux contrats
internes de `HackerOs.Platform.Core` (`IWireTopicMessageBus`,
`IWireEventBus`). Ils indexent les abonnements d'un worker par **nom de type**
(`"{FullName}, {AssemblyName}"`, `WirePayloadNames.Of`) :

- une publication du fil principal livre l'objet à un abonnement wire, qui le
  sérialise pour son worker ;
- une publication d'un worker (`PublishWire`) transmet le JSON tel quel aux
  autres workers et ne le matérialise en objet **qu'une fois par type** pour les
  abonnés du fil principal ;
- les mêmes règles d'autorisation qu'un appel typé s'appliquent (sujet d'une
  autre app, canal partagé verrouillé).

`AppEventGateway` expose ces membres avec l'identité du processus ; le
protocole n'y accède que par le contexte de confiance de `RemoteProcessSession`.

### 2. Opérations ajoutées

| Opération | Sens | Forme |
|---|---|---|
| `ev.sub` | worker → OS, `call` | sujet ou type, capacité ; l'abonnement existe avant le retour |
| `ev.unsub` | worker → OS, `note` | identifiant d'abonnement |
| `ev.pub` | worker → OS, `note` | sujet, nom du type, JSON |
| `ev.chan` | worker → OS, `note` | sujet, politique |
| `ev.ack` | worker → OS, `note` | identifiant, nombre de messages livrés |
| `watch.open` | worker → OS, `call` | chemin, portée |
| `ev` | OS → worker | identifiant, JSON, sujet, app éditrice, date |

Le crédit de 256 messages en vol par abonnement (ADR 0124 §1) vaut pour
**tous** les abonnements, canaux bornés compris : le canal garde sa capacité
côté OS (perte du plus ancien quand il déborde), et le worker rend le crédit
par `ev.ack` après chaque livraison.

### 3. Publication depuis un worker (écart)

`Publish` d'un worker **poste** la publication (`ev.pub`, une `note`) et
renvoie aussitôt `TopicPublishResult.Delivered` sans fautes. Le vrai résultat
n'est connu que de l'OS : un refus (sujet d'une autre app, canal verrouillé)
est écrit dans le journal de l'app (`/var/log/apps/<app>.log`). L'ADR 0124
prévoyait d'attendre le résultat, ce qui est impossible sans changer le
contrat synchrone (contrainte 1 et 2). La question est posée à Benoit (voir
« Question ouverte »).

Une publication d'un type d'événement **noyau** (`Publish<TEvent>`) depuis un
worker renvoie une liste de fautes vide sans rien envoyer : ces événements sont
publiés par l'OS, pas par les apps.

### 4. Refus d'un abonnement (écart)

`Subscribe` est synchrone lui aussi : le worker rend l'abonnement tout de suite
et l'ouvre en tâche de fond.

- Un abonnement par **canal** refusé termine le canal avec l'exception
  (`AppGatewayAccessDeniedException`) : le `await foreach` de l'app la reçoit.
- Un abonnement par **callback** refusé est écrit dans le journal de l'app.

### 5. `RegisterSharedChannel`

Il passe par une `note` (`ev.chan`) ; seule la portée de ressource
`VirtualPath` est acceptée par le protocole (les autres portées n'ont pas de
codec) ; un refus est écrit dans le journal de l'app.

### 6. Codecs intégrés

Liste fixe, encodée à la main (`WirePayloadCodec`, sans réflexion) :
`FileSystemChangeEvent`, `AppDisabledEvent`, `ProcessStateChangedEvent`,
`SessionActivatedEvent`, `SessionLoggedOutEvent`, `SessionShutDownEvent`,
`DiagnosticLogClearedEvent`. Un autre type public de l'OS qui doit traverser
reçoit son codec dans ce fichier. Les types d'apps passent par
`[assembly: HackWireContext(typeof(...))]` (ADR 0124 §2) ; le contexte est créé
par son constructeur sans paramètre, d'où l'annotation
`DynamicallyAccessedMembers` de l'attribut (compatible avec le trimming).

### 7. Ordre des livraisons

Dans un worker, les livraisons sont toujours **asynchrones**, même vers un
abonnement du même processus : la publication fait l'aller-retour par l'OS.
Sur le fil principal, la livraison reste synchrone comme avant.

### 8. Pool et assemblies connues

- `WorkerPoolOptions.KnownAssemblies` (nouveau, vide par défaut) porte la liste
  du build ; l'Ecosystem la remplit avec `LazyAssemblyNames` du catalogue. Elle
  est envoyée au worker dans `init` (`known`).
- Une assembly hors de la liste et absente du runtime du worker est refusée :
  le worker écrit l'erreur sur la sortie d'erreur du processus et le termine
  avec le code **127** (comme un shell pour une commande introuvable). Une liste
  vide (hôte qui ne la renseigne pas) n'impose aucune restriction.
- L'Ecosystem référence désormais `HackerOs.Samples.TerminalApp` : sa commande
  `sample-cmd` sert de commande tierce chargée à la demande dans le test des
  hôtes (`HostSmoke`). Comme toute commande, elle est masquée du menu Démarrer
  (`LaunchVisibility = Hidden`) et reste désinstallable. `test/test` n'est pas changé.

### 9. Revue des apps en `MainThread`

Les 16 commandes en `mainThread` de `HackerOs.Commands` ont été relues ; leur
raison tient toujours (commandes intégrées du shell, écran alternatif, `sudo`,
`passwd`, `hackbackup`). Aucune ne dépendait de `HackEvents` ou de `Watch`.

## Question ouverte

Publication synchrone depuis un worker (§3), posée à Benoit avec ces choix :

- **A (défaut, en place)** : résultat provisoire `Delivered`, refus au journal.
- **B** : ajouter `PublishAsync` au SDK 1.2, qui renvoie le vrai résultat ;
  `Publish` garde le comportement A.
- **C** : `Publish` vers un sujet qui n'appartient pas à l'app lève
  `NotSupportedException` dans un worker.

Sa réponse sera consignée ici ou dans une ADR qui remplace ce paragraphe.

## Conséquences

- Une app qui doit savoir si sa publication a été refusée lit son journal, ou
  publie depuis le fil principal, tant que la question n'est pas tranchée.
- Le service d'exemple et `hackwebd` tournent en worker dédié.
- Tests : `RemoteEventsTests` (parité fil principal / worker pour les sujets,
  les canaux, les événements noyau, `Watch` et les refus ; charge non déclarée ;
  désabonnement au `kill` ; service d'exemple ; `sample-cmd` chargé à la
  demande ; refus d'une assembly inconnue), `WireTopicMessageBusTests`.

## Références

- ADR 0070, 0072, 0080, 0113, 0124, 0132.
- [`worker-processes.md`](../worker-processes.md).
