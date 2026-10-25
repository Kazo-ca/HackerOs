> [English version](../../../../en/wasm/dev/adr/0071-always-ready-worker.md)

# ADR 0071 : toujours un worker prêt pour la prochaine demande

## Statut

Accepté le 2026-10-16.

- **Phase du plan :** phase 5 — exécution des apps
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Complète :** ADR 0070. Remplace ses sections 2 (moment du démarrage du pool)
  et 3 (réserve réservée aux processus dédiés) sur les points ci-dessous ; le
  reste de l'ADR 0070 est inchangé.

## Contexte

L'ADR 0070 démarre le pool au boot et garde une réserve pour les seuls processus
dédiés. Deux cas laissent encore une demande attendre un démarrage à froid (0,5
à 2 s) :

- tous les workers du pool sont occupés, par exemple l'un d'eux est bloqué par
  une boucle : une nouvelle commande attendrait derrière lui ;
- la réserve vient d'être prise par un service, et une deuxième demande arrive
  avant qu'elle soit remplacée.

Benoit veut qu'**il y ait toujours un worker disponible pour la prochaine
demande**, une fois le système démarré.

## Décision

### 1. Invariant

Une fois le système démarré, l'OS garde **au moins un worker prêt** : runtime
chargé, assemblies du SDK chargées, aucun processus. Chaque fois qu'un worker
prêt est pris, l'OS en lance un autre en arrière-plan, tout de suite.

### 2. « Système démarré »

Le démarrage des workers ne doit pas ralentir le boot. Le premier worker prêt
est lancé quand la session est ouverte, le bureau affiché et les apps de
démarrage lancées (signal de fin de boot de `EcosystemBootCoordinator`), puis
dès que le navigateur est inactif (`requestIdleCallback`, avec un délai maximal
de 2 s). Le pool de l'ADR 0070 §2 démarre au même moment, worker par worker,
derrière le premier worker prêt.

Avant ce signal, une demande qui a besoin d'un worker en démarre un elle-même
et l'attend.

### 3. Qui prend le worker prêt

- un processus `Dedicated` (ADR 0070 §3) ;
- une commande ou un pipeline quand **aucun worker du pool n'est libre**
  (aucun n'a son thread disponible : il exécute une tranche, ou il est « Not
  responding »). Le worker prêt rejoint alors le pool pour la durée de ce
  processus : le pool déborde au-delà du nombre de cœurs virtuels, comme une
  machine qui ajoute des threads matériels sous charge, et revient à sa taille
  quand le worker en trop est resté vide 30 s ;
- un worker du pool terminé pendant une escalade (ADR 0069 §3, étape 4) : le
  worker prêt le remplace immédiatement.

### 4. Plafond

Le nombre total de workers est plafonné à `navigator.hardwareConcurrency` (au
moins 2). Au plafond, l'invariant est suspendu : une nouvelle demande attend
qu'un worker se libère, et le journal `/var/log/syslog` le note une fois
(« worker pool saturated »). L'invariant reprend dès qu'un worker se libère.

### 5. Visibilité

- `/proc/hackeros/workers` (nouveau, lecture seule) liste chaque worker : rôle
  (`pool`, `dedicated`, `ready`), état, processus hébergés, mémoire du runtime ;
- le gestionnaire de tâches affiche les workers dans l'onglet Performance, avec
  les cœurs.

## Conséquences

- Une demande après le boot ne paie jamais un démarrage à froid, sauf au plafond.
- Le coût mémoire de base devient « pool + 1 worker », soit environ
  (`CpuCapacity` + 1) × 15 à 40 Mo.
- Les tests du chantier `5-worker-processes` couvrent l'invariant (prise,
  remplacement, débordement, retour à la taille, plafond, demande avant la fin
  du boot).

## Références

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `OS/HackerOs.Ecosystem/EcosystemBootCoordinator.cs`.
- ADR 0067, 0069, 0070.
