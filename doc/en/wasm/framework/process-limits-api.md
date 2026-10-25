# Limites de processus côté apps

Ce document décrit ce qu'une app voit et peut faire avec les limites de son processus (`HackProcess`, `/proc/<pid>/limits`). La mise en œuvre (tables, contrôleur, lecture de `limits.conf`, commandes du shell) est dans [`../dev/process-limits.md`](../dev/process-limits.md).

## Rôle

Chaque processus porte quatre limites façon `setrlimit(2)`, avec une valeur
souple (celle qui s'applique) et une valeur dure (son plafond) :

| Limite | `ulimit` | `/proc/<pid>/limits` | Défaut (souple / dure) | Effet quand elle est atteinte |
|---|---|---|---|---|
| `cpu` | `-t` (secondes) | `Max cpu time` | illimité / illimité | souple : `SIGXCPU` (sortie 152, « CPU time limit exceeded ») ; dure : `SIGKILL` (137, « Killed ») |
| `rss` | `-m` (Kio) | `Max resident set` | illimité / illimité | `SIGKILL` (137) |
| `nofile` | `-n` | `Max open files` | 1024 / 524288 | ouverture refusée : `EMFILE`, « Too many open files » |
| `nproc` | `-u` | `Max processes` | 4096 / 4096 | lancement refusé : `EAGAIN`, « hsh: fork: Resource temporarily unavailable » |

## Héritage et application

Ce sont les défauts du noyau Linux. Un processus hérite des limites de son
parent ; un processus sans parent prend celles de la session, lues dans
`/etc/security/limits.conf` à la connexion.

L'application est **stricte dans un Web Worker** (l'OS termine le worker) et
**au mieux sur le fil principal** (vérifiée entre deux tranches de travail).
Les limites `cpu` et `rss` ne s'appliquent que là où le CPU et la mémoire sont
mesurés ([`resource-accounting.md`](../dev/resource-accounting.md)) ; sinon
`/proc/<pid>/limits` les dit inactives.

## Descripteurs

- Un descripteur = une lecture ou un flux d'écriture ouvert ; les écritures
  d'un bloc, les listes de dossiers et les sockets ne comptent pas.

## Fichier `/proc/<pid>/limits`

```text
Limit                     Soft Limit           Hard Limit           Units     
Max cpu time              unlimited            unlimited            seconds   
…
Max open files            1024                 524288               files     
# enforcement: strict (worker)
# cpu, rss: inactive (not measured)
```

La ligne `# enforcement:` vaut `strict (worker)`, `best-effort (main thread)`
ou `off (HackerOs:ResourceLimits:Enabled=false)`. `/proc/<pid>/fd/` (dossier
`0500`) liste les descripteurs ouverts sous forme de liens.

## API du SDK

```csharp
HackProcess self = await HackProcess.GetCurrentProcessAsync();
HackResourceLimits limits = await self.GetResourceLimitsAsync();
await self.SetResourceLimitAsync(ResourceLimitKind.NoFile, 256, limits.NoFile.Hard);
```

Erreurs : `ArgumentException` (`EINVAL`), `HackUnauthorizedAccessException`
(`EPERM`), `InvalidOperationException` (`ESRCH`).
