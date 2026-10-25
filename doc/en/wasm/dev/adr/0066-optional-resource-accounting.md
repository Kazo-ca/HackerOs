# ADR 0066 : mesure des ressources optionnelle, au build et au démarrage

## Statut

Accepté le 2026-10-16.

- **Phase du plan :** phase 5 — exécution des apps
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Encadre :** ADR 0067 (temps CPU mesuré) et ADR 0068 (mémoire mesurée).
- **S'appuie sur :** ADR 0012 (simulateur de ressources, `ResourceProfile`),
  ADR 0057 (`/proc/stat`, `ResourceSamplingService`).

## Contexte

Aujourd'hui le CPU et la mémoire d'un processus sont **simulés** : le
`ResourceSimulator` tire chaque échantillon du `ResourceProfile` déclaré par
l'app (ADR 0012), et `/proc/<pid>/status` affiche ces valeurs. La phase 5 ajoute
une **mesure réelle** du temps d'exécution et des allocations de chaque
processus (ADR 0067 et 0068).

Benoit veut pouvoir couper cette mesure : elle ajoute du travail à chaque
changement de contexte (surtout pour le CPU), et un usage que nous n'avons pas
prévu pourrait en souffrir. Elle doit donc se désactiver **au build** (le code
n'est pas livré) et **au démarrage** (le code est livré mais n'est jamais
branché).

Contrainte : les hôtes publient avec `PublishTrimmed=False`
(`OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`). Un simple feature switch
.NET (`[FeatureSwitchDefinition]`) ne retirerait donc aucun code du binaire.

## Décision

### 1. Deux mesures indépendantes

La mesure du CPU (ADR 0067) et celle de la mémoire (ADR 0068) ont chacune leur
interrupteur. Couper l'une ne touche pas l'autre.

### 2. Au build : un projet à part, référencé sous condition

Le code de mesure vit dans un projet dédié,
`Platform/HackerOs.Platform.ResourceAccounting`, qui dépend de Platform Core
et non l'inverse. Platform Core ne connaît que des contrats
(`ICpuTimeAccountant`, `IMemoryAccountant`, dans Simulation Abstractions) et
leurs implémentations nulles.

Deux propriétés MSBuild, lues par `build/HackerOs.ResourceAccounting.targets`
importé par chaque hôte :

```xml
<HackerOsCpuAccounting>true</HackerOsCpuAccounting>       <!-- défaut -->
<HackerOsMemoryAccounting>true</HackerOsMemoryAccounting> <!-- défaut -->
```

- Si les deux valent `false`, l'hôte ne référence pas le projet : le code de
  mesure **n'est pas dans le binaire**.
- Chaque propriété devient aussi une `RuntimeHostConfigurationOption`
  (`HackerOs.ResourceAccounting.Cpu.IsSupported`,
  `HackerOs.ResourceAccounting.Memory.IsSupported`), lue par
  `AppContext.TryGetSwitch`. Le jour où la publication active le trimming, les
  propriétés de garde marquées `[FeatureSwitchDefinition]` retirent aussi le
  code d'une mesure coupée alors que l'autre reste.

### 3. Au démarrage : `appsettings.json`

L'hôte Ecosystem reçoit un `wwwroot/appsettings.json` (lu par
`WebAssemblyHostBuilder.Configuration`) ; `test/test` et `HackerOs.Server`
lisent le leur.

```json
{
  "HackerOs": {
    "ResourceAccounting": {
      "Cpu":    { "Enabled": true, "UnresponsiveThresholdMs": 5000 },
      "Memory": { "Enabled": true }
    }
  }
}
```

- La valeur est lue **une fois, à la composition des services**. Désactivée,
  l'OS enregistre l'implémentation nulle et **n'installe aucun crochet** : pas
  de test « activé ? » dans le chemin chaud.
- Une mesure coupée au build l'emporte toujours : `Enabled: true` est alors
  ignoré, avec une ligne d'information dans `/var/log/syslog`.
- Changer la valeur demande un rechargement de la page, comme un paramètre
  de démarrage du noyau.
- Ce réglage est un réglage d'**hôte**, pas un paramètre de l'OS simulé : il
  ne va pas dans `/etc/hackeros` (ADR 0044), que l'utilisateur de l'OS
  pourrait modifier.

Valeur par défaut de `HackerOs.Server` : les deux mesures **coupées** tant que
l'ADR 0067 n'a pas été validé sur un hôte multithread (section 6 de l'ADR
0067).

### 4. Mesure coupée : retour exact au comportement actuel

Quand une mesure est coupée, sa valeur revient au `ResourceSimulator`, comme
aujourd'hui. Rien ne change pour les apps ni pour `/proc`, sauf un indicateur :

- `/proc/<pid>/status` ajoute `CpuSource: simulated|measured` et
  `MemSource: simulated|measured` ;
- le gestionnaire de tâches affiche une mention discrète « estimated » à côté
  des colonnes simulées.

Les processus du contenu de jeu, qui n'exécutent pas de code, restent
toujours simulés.

## Conséquences

- Un hôte peut livrer un binaire sans aucune mesure, ou la couper par
  configuration sans recompiler.
- Les tests couvrent les quatre combinaisons (CPU et mémoire, activé ou non)
  et vérifient qu'aucun crochet n'est installé quand la mesure est coupée.
- `system-metrics.md` documente les deux sources et leur indicateur.

## Références

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- ADR 0012, 0044, 0057, 0067, 0068.
