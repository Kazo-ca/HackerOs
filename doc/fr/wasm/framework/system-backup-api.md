> [English version](../../../en/wasm/framework/system-backup-api.md)

# Sauvegarde système côté apps

Ce document décrit ce qu'une app voit de la sauvegarde de la machine : l'API `HackSystemBackup`, l'archive `.hosbak`, la capacité `system.backup` et les codes d'erreur d'espace. La mise en œuvre (séquence, espace de stockage, cache PWA, montées de version IndexedDB, tests) est dans [`../dev/system-backup.md`](../dev/system-backup.md).

## Archive `.hosbak`

Le JSON `formatVersion: 1` de `IndexedDbBackupService` (version du schéma,
tous les magasins, SHA-256), compressé en gzip. `ReadArchive` reconnaît le gzip
à ses deux premiers octets (`1F 8B`) et accepte aussi le JSON brut. Une archive
abîmée, vide, falsifiée (empreinte fausse) ou d'une autre version de schéma est
refusée **avant** toute écriture, avec un message qui nomme les deux versions :

```text
This backup uses database schema version 3; this HackerOS uses version 4. It cannot be restored here.
```

Nom : `hackeros-AAAAMMJJ-HHMMSSZ.hosbak` (heure UTC de l'export).

## API du SDK

```csharp
// Capacité system.backup (dangereuse) et un administrateur à chaque appel.
HackBackupInfo backup = await HackSystemBackup.CreateAsync(AppContext);
HackBackupInfo? restored = await HackSystemBackup.RestoreAsync(AppContext, HackBackupRestoreMode.Replace);
// restored == null : l'utilisateur a fermé le sélecteur ; sinon la machine redémarre.
```

| Membre | Effet |
|---|---|
| `IsSupported`, `IsSupportedFor(context)` | `false` sur un hôte sans IndexedDB. |
| `CreateAsync` | Demande un administrateur, exporte, télécharge l'archive sur l'hôte, journalise. |
| `RestoreAsync(mode)` | Ouvre le sélecteur de l'hôte, valide l'archive, **puis** demande un administrateur, ferme la session, restaure, redémarre. |
| `HackBackupInvalidException` | Archive refusée (sous-classe de `HackIOException`). |

L'ordre de `RestoreAsync` est voulu : le navigateur n'ouvre son sélecteur que
juste après un geste de l'utilisateur (clic, ou Entrée dans le terminal), et
un mauvais fichier ne doit coûter à personne un mot de passe.

## Capacité

`HackSystemBackup` exige la capacité `system.backup`, qui est dangereuse (ADR 0049). Seule l'app système Paramètres la déclare. Un administrateur est demandé à chaque appel.

## Règles

- Tester `HackSystemBackup.IsSupported` (ou `IsSupportedFor(context)`) avant d'afficher une action : sur un hôte sans IndexedDB il vaut `false`, et l'onglet Backup de Paramètres est masqué.
- Déclencher `RestoreAsync` depuis un geste de l'utilisateur (clic, ou Entrée dans le terminal) : le navigateur n'ouvre son sélecteur que juste après un geste. La validation de l'archive passe avant la demande d'administrateur, si bien qu'un mauvais fichier ne coûte pas de mot de passe.
- Traiter `null` comme « l'utilisateur a fermé le sélecteur » : la machine ne redémarre pas.
- Traiter `HackBackupInvalidException` : c'est une `HackIOException`, et l'archive refusée n'a rien écrit.

## Réserve de root

Dans la réserve, `FileSystemService` refuse `Create`, `Write` et `Copy` d'un
appelant dont l'autorité effective est sous Administrateur, avec `NoSpace`.
Le système (journaux, démarrage), root (`sudo`) et l'administrateur écrivent
jusqu'à la vraie limite du navigateur. Les montages virtuels (`/proc`, `/dev`)
ne sont jamais concernés.

## Codes `NoSpace` et `FileTooLarge`

| Origine | Code | Message du shell |
|---|---|---|
| `QuotaExceededError` d'IndexedDB (transaction annulée en entier) | `NoSpace` | `No space left on device` |
| Réserve de root | `NoSpace` | `No space left on device` |
| Contenu de plus de 16 Mio | `FileTooLarge` | `File too large` |

`HackFile`/`HackFileStream` lèvent `HackIOException` avec `ErrorCode` ;
`ShellErrorText.Describe` et les commandes (`chmod`, `mkdir`…) affichent le
message Linux.
