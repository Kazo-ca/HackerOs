# Journaux des apps (`/var/log`) : API et fichiers produits par une app

Ce document décrit les fichiers de journal qu'une app, une commande ou un site produit, les API qui les écrivent et ce qui a été retiré du SDK. L'implémentation (`LogFileWriter`, rotation, canal, Error Log Viewer) et les journaux du système (`syslog`, `kern.log`, `auth.log`, `audit.log`) sont dans [`dev/logging.md`](../dev/logging.md).

## Fichiers produits par une app

| Fichier | Contenu | Mode |
|---|---|---|
| `/var/log/apps/<appId>.log` | `IAppExecutionContext.Logging` de l'app | `system:system 0644` |
| `/var/log/<démon>/<fichier>.log` | `IAppLoggingGateway.AppendToLogFile` d'une **app système** seulement, ligne écrite telle quelle (ADR 0080 §3) ; par exemple `/var/log/hackweb/error.log` de `hackwebd` ([`static-sites.md`](../dev/static-sites.md)) | `system:system 0644` |
| `/var/log/hackweb/<appId>.log` | Journal de débogage d'un **site** (app `Web`) qui l'active par `builder.Logging.EnableDebugLog()` : `ILogger<T>`, requêtes, exceptions, par `IAppLoggingGateway.AppendToSiteDebugLog` (ADR 0088 §5, [`asp-hack-site-data.md`](asp-hack-site-data.md)) | `system:system 0644` |

## Écrire dans un journal

Dans une app, `IAppExecutionContext.Logging` (type `IAppLoggingGateway`) écrit dans `/var/log/apps/<appId>.log`. Une app système écrit une ligne telle quelle dans un fichier de son démon avec `AppendToLogFile(relativePath, line)`. Un site qui active le journal de débogage (`builder.Logging.EnableDebugLog()`) écrit avec `AppendToSiteDebugLog(line)`.

## Format des lignes

- `LogLineFormat` (`Shared/HackerOs.Simulation.Abstractions/Diagnostics/`) :
  `Format`, `FormatAudit` et `TryParse`. Une entrée tient sur une ligne :
  `2026-10-10T03:11:42.123Z WARN  org.hackeros.text-editor[42]: message {clé=valeur}`.
  Le message échappe `\`, `\n`, `\r`, `\t` et aussi `{` (en `\{`), pour qu'un
  message ne soit jamais relu comme un bloc de propriétés. Dans ce bloc, les
  clés et valeurs échappent en plus `,`, `=` et `}`. Les espaces d'une source
  deviennent `_`.

## Rotation

- `LogRotationPolicy` : `MaxFileBytes` = 1 Mio, `MaxArchives` = 5. Rotation à
  la `logrotate` (`.5` supprimé, `.4`→`.5` … `<f>`→`.1`, nouveau `<f>` vide).
  Un fichier vide ne tourne jamais : un lot plus gros que 1 Mio est écrit entier.

## Délais et pertes

Les lignes d'un journal sont vidées par lot : toutes les 1 s de temps simulé, dès 100 lignes en attente, à la déconnexion et à l'arrêt. Au-delà de 10 000 lignes en attente, les plus anciennes sont perdues ; une ligne `WARN logging` le signale au vidage suivant.

## Ce qui a été retiré

- `IAppDiagnosticsGateway`, `AppDiagnosticsGateway` et `IAppExecutionContext.Diagnostics`
  sont supprimés. Lire les journaux, c'est lire des fichiers.
- `diagnostics.read` et `diagnostics.clear` sont retirées du catalogue par
  l'ADR 0049 ([`capabilities.md`](capabilities.md)).
