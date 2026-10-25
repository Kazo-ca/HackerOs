# ADR 0146 : `HackCrossOriginIsolation`, l'état de l'isolation dans le SDK

## Statut

Accepté le 2026-10-18.

- **Réponse de Benoit :** option **C** (« API du SDK ») de la carte « Laisser
  Diagnostic lire /proc (capacité dangereuse) pour afficher l'isolation ? »
  (2026-10-18, 16:34Z, fil « Exec 9 : isolation cross-origin »).
- **Remplace :** la dernière puce de l'ADR 0143 §8 (Diagnostic lit
  `/proc/hackeros/workers` et déclare `filesystem.system.read`). Le reste de
  l'ADR 0143 §8 reste vrai : la sonde, les raisons, la ligne du journal et les
  lignes de `/proc/hackeros/workers` ne changent pas.
- **Version :** la carte disait « au SDK 1.2 », mais la 1.2.0 est déjà publiée
  (ADR 0132 §2, chantier `exec/10-final`). Comme l'ADR 0145, l'ajout reste
  dans `PublicAPI.Unshipped.txt` et part avec la prochaine mineure (1.3.0),
  selon SemVer ([`sdk-packages.md`](../../framework/sdk-packages.md)).

## Contexte

L'ADR 0131 §5 veut que Diagnostic montre si la page est isolée. L'ADR 0143 §8
le faisait lire `/proc/hackeros/workers` par `HackFile`. Or lire `/proc`
demande `filesystem.system.read`, une capacité dangereuse (ADR 0049).
Diagnostic avait donc une deuxième capacité dangereuse, qu'un administrateur
qui l'avait déjà approuvé devait approuver au lancement suivant. Trois choix ont
été proposés : garder (A), retirer le badge (B), ajouter une petite API publique
non dangereuse (C). Benoit a choisi C.

## Décision

1. **API** (`HackerOs.AppSdk`, espace `Diagnostics`) :
   - `HackCrossOriginIsolation.GetStatusAsync(CancellationToken)` rend un
     `HackCrossOriginIsolationStatus` : `IsIsolated`, `Reason`
     (`HackCrossOriginIsolationReason` : `None`, `Setting`, `MissingHeaders`,
     `InsecureContext`, `Browser`, `NotBrowser`), `Setting`
     (`HackCrossOriginIsolationSetting` : `Auto`, `On`, `Off`),
     `EmbedderPolicy`, `SharedPipes`, `SynchronousWorkerCalls`,
     `BrowserMemoryMeasurement`, `TimerResolution` ;
   - ce sont les faits des premières lignes de `/proc/hackeros/workers`, que
     l'OS a décidés au démarrage ; ils ne changent pas avant un rechargement de
     la page.
2. **Aucune capacité.** Comme `uname` ou `/proc/cpuinfo` sous Linux, l'état
   décrit la machine, pas une autre app ni un fichier d'utilisateur. Toute app
   peut le lire. `/proc` reste protégé par `filesystem.system.read`.
3. **Passerelle** `IAppCrossOriginIsolationGateway`, rendue par
   `IAppExecutionContext.CrossOriginIsolation`. Le membre a une implémentation
   par défaut, qui lève `NotSupportedException`, comme `Drives` : une
   implémentation existante d'`IAppExecutionContext` compile toujours, et une
   app 1.0, 1.1 ou 1.2 n'est pas touchée.
4. **Sur le fil principal**, `AppCrossOriginIsolationGateway` (Platform Core,
   espace `Isolation`) lit `CrossOriginIsolationState`. L'hôte la donne à la
   fabrique par `SystemMetricsServices.CrossOriginIsolation` (propriété `init`,
   ajout non cassant). Avant la sonde et hors navigateur, la raison est
   `NotBrowser`.
5. **Dans un worker**, c'est un appel (`call`) de l'opération `coi.get` ; l'OS
   répond avec les huit champs. L'appel est asynchrone, donc il marche avec ou
   sans isolation.
6. **Diagnostic** lit l'état par `HackCrossOriginIsolation` et ne déclare plus
   `filesystem.system.read`. Sa seule capacité dangereuse redevient
   `system.storage.inspect`, comme avant `exec/9`. L'infobulle du badge garde
   le texte de `/proc/hackeros/workers` (mêmes clés, mêmes valeurs). Le badge
   s'affiche aussi sans pool de workers ; il disparaît seulement si le contexte
   ne fournit pas la passerelle.
7. **Exemple** : `sample-cmd --isolation` écrit `isolation: yes|no <raison>`.

## Conséquences

- Un administrateur qui a approuvé Diagnostic depuis `exec/9` garde une
  décision pour `filesystem.system.read` dans
  `/etc/hackeros/app-permissions.config`. Elle ne sert plus, puisque le
  manifeste ne déclare plus cette capacité. Aucune nouvelle fenêtre
  d'approbation n'apparaît.
- Nouvelles lignes dans `Shared/HackerOs.AppSdk/PublicAPI.Unshipped.txt` et
  `Platform/HackerOs.Platform.Core/PublicAPI.Unshipped.txt` ; `HackerOsVersion`
  reste `1.2.0`.
- Tests :
  - `CrossOriginIsolationStatusTests` (WorkerHost) : même réponse sur le fil
    principal et en worker, sans capacité ; aller-retour de chaque champ par le
    protocole ;
  - `CrossOriginIsolationTests` (Platform Core) : chaque raison et le réglage ;
  - `BuildKnownLazyAppsTests` : Diagnostic n'a plus que `system.storage.inspect`
    comme capacité dangereuse.

## Références

- ADR 0049, 0057, 0131, 0132, 0143, 0145.
- [`worker-processes.md`](../worker-processes.md), [`app-sdk.md`](../../framework/app-sdk.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md), [`sdk-packages.md`](../../framework/sdk-packages.md),
  [`capabilities.md`](../../framework/capabilities.md).
