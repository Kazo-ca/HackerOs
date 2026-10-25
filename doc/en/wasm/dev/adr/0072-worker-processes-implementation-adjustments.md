# ADR 0072 : ajustements de la mise en œuvre des processus dans des Web Workers

## Statut

Accepté le 2026-10-16.

- **Phase du plan :** phase 5 — exécution des apps
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)), chantier
  `rebuild/5-worker-processes`.
- **Complète :** ADR 0070 et ADR 0071. Remplace l'ADR 0070 §1 (scripts shell),
  §2 (tubes en mémoire du worker) et précise §4, §5 et l'ADR 0071 §3 sur les
  points ci-dessous ; le reste de ces ADR est inchangé.

## Contexte

La mise en œuvre de l'ADR 0070 ([`worker-processes.md`](../worker-processes.md))
a buté sur des points que les ADR ne tranchent pas, ou tranchent d'une façon
qui demanderait de déplacer le shell dans les workers. Ils sont consignés ici
plutôt que de modifier les ADR acceptés.

## Décision

### 1. Le shell reste sur le fil principal

- `hsh`, son interpréteur, ses variables, ses jobs et ses **scripts** restent
  sur le fil principal ; seules les commandes externes qu'il lance vont dans un
  worker. Un script ne bloque donc pas l'OS : chacune de ses commandes tourne
  dans un worker, et la boucle d'un script rend la main entre deux commandes.
- Les commandes intégrées (`cd`, `export`, `jobs`, `kill`, `read`, `exit`…) et
  les commandes qui pilotent le shell, l'élévation ou l'écran (`sudo`, `nano`,
  `top`, `htop`, `watch`, `which`, `help`) déclarent `MainThread`, avec la
  raison en commentaire dans leur définition. `kill` répond donc même quand
  tous les workers sont bloqués.

### 2. Tubes d'un pipeline

Les étapes d'un pipeline sont bien placées sur le même worker (ADR 0070 §2),
mais leurs **tubes restent ceux de l'OS** sur le fil principal : chaque étape
écrit et lit par messages, avec un crédit de 64 Kio en vol par flux. Garder le
tube dans le worker demanderait d'y déplacer l'interpréteur (point 1), et
`/proc/<pid>/fd`, les octets comptés et `SIGPIPE` resteraient à reconstruire.
Un consommateur qui se ferme fait lever `RemoteBrokenPipeException` (une
`HackIOException`) chez le producteur, qui sort avec 141.

### 3. Ce qu'un worker n'offre pas

Dans un worker, les passerelles des événements typés (`HackEvents`), des
abonnements de fichiers (`Watch`), de l'hôte de shell, de l'administration et
de la page HTTP du navigateur simulé lèvent `NotSupportedException`. Une app
qui en a besoin déclare `MainThread` (ADR 0070 §1, dernière phrase). C'est le
cas du service d'exemple, qui publie des événements typés.

### 4. Tuer un processus depuis un worker

`IAppProcessGateway` gagne `KillAsync(pid, reason, ct)`, méthode par défaut qui
retombe sur `Kill`. `HackProcess.KillAsync` l'utilise : dans un worker, l'appel
attend la réponse de l'OS au lieu d'un appel synchrone impossible.

### 5. Escalade

- Étape 3 (ADR 0069) dans un worker : l'OS envoie `kill` au worker, qui jette
  le processus et confirme. Sans confirmation en **1 s**, l'OS passe à l'étape 4
  et termine le worker (le thread du worker est bloqué).
- Le shell écrit « Killed » sur la sortie d'erreur d'une commande sortie avec
  137 et la raison `Killed` ou `Timeout`, qu'elle ait tourné dans un worker ou
  non (ADR 0070 §5 le prévoyait pour les voisins).

### 6. « Libre » et temps d'un worker bloqué

- Un worker du pool est **libre** (ADR 0071 §3) quand il est prêt et que son
  dernier battement de cœur date de moins de 750 ms (3 battements) ; un worker
  en train d'exécuter une longue tranche cesse de battre, il n'est donc plus
  libre.
- Le temps pendant lequel un worker qui héberge des processus ne bat plus est
  compté comme **occupé** : `utime` de ses processus (partagé), cœur de
  `/proc/stat`, « CPU » du gestionnaire de tâches. Sans cela, une boucle
  infinie afficherait 0 % jusqu'à son prochain battement.
- `cpu0` de `/proc/stat` additionne le fil principal et le worker du cœur 0.

### 7. Hôtes

`test/test` exécute côté navigateur le `Program.cs` de l'Ecosystem : il a les
workers comme l'Ecosystem. `HackerOs.Server` n'en a pas et l'écrit une fois
dans `/var/log/syslog` (ADR 0070 §6) ; `/proc/hackeros` n'y existe pas.

## Conséquences

- Un pipeline coûte un aller-retour de messages par écriture, absorbé par le
  crédit de 64 Kio ; la parité de sortie est testée pour les 63 commandes.
- 14 commandes restent sur le fil principal ; elles ne font que piloter le
  shell ou l'écran.
- Les services qui ont besoin d'événements typés restent sur le fil principal
  jusqu'à ce qu'une passerelle distante d'événements existe.

## Références

- [`worker-processes.md`](../worker-processes.md)
- ADR 0067, 0069, 0070, 0071.
