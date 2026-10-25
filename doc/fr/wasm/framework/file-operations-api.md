> [English version](../../../en/wasm/framework/file-operations-api.md)

# Opérations sur les fichiers : API `HackFileOperations`

Ce document décrit `HackFileOperations`, l'API du SDK qui copie, déplace, importe et envoie à la corbeille des fichiers, avec ses règles de nommage, de conflit et d'arrêt. Le contrôle de fichiers de l'explorateur, qui s'appuie sur ces opérations, est dans [`dev/file-explorer.md`](../dev/file-explorer.md).

## Transferts (`HackFileOperations`, ex-`FileTransfer`)

```csharp
HackFileTransferResult copied = await HackFileOperations.CopyAsync(paths, "/home/benoit/Backup");
HackFileTransferResult moved = await HackFileOperations.MoveAsync(paths, destination, Dialogs.AsFileConflictResolver(AppContext));
HackFileTransferResult imported = await HackFileOperations.ImportAsync(hostFiles, destination);
HackFileTransferResult trashed = await HackFileOperations.MoveToTrashAsync(paths);
string? summary = moved.DescribeFailures("move");   // « Could not move 'x': permission denied. »
```

Règles :

| Cas | Comportement |
|---|---|
| Copie, nom pris (y compris coller dans le même dossier) | `nom (copy).ext`, puis `nom (copy 2).ext`… ; un nom déjà suffixé continue la série. Pas d'extension séparée pour un dossier ou un fichier caché (`.bashrc (copy)`). |
| Déplacement, nom pris | Fenêtre « Replace » (supprime la destination, dossier compris), « Keep both » (nom de copie), « Skip » ; « Apply to all » vaut pour le reste de l'opération. Fermer la fenêtre = « Skip ». |
| Couper puis coller dans le dossier d'origine | Rien. |
| Dossier vers lui-même ou un descendant | Refusé avant toute écriture ; les autres éléments continuent. |
| Fichiers de l'hôte | Écrits comme une copie (nom de copie si pris), 256 Mio au plus par fichier. |
| Erreur | Une raison par élément (`permission denied`, `read-only file system`, `no longer exists`…), sans arrêter les autres. |
| Arrêt demandé (ADR 0069 : jeton annulé, `kill`, « End task », déconnexion) | Entre deux éléments (`HackProcess.CheckpointAsync`, qui rend aussi la main à l'interface) : l'élément en cours, atomique, se termine ; les suivants sont rapportés « stopped before this item » et `FileTransferResult.Cancelled` est vrai. Rien n'est à moitié copié ni perdu en silence. La fenêtre de l'explorateur fait attendre l'OS jusqu'à la fin de l'élément en cours. |

Après un collage d'éléments coupés sans échec, le presse-papiers est vidé.
