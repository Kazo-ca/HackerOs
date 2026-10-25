> [English version](../../../../en/wasm/dev/adr/0149-sdk-2-0-file-view-removal.md)

# ADR 0149 : SDK 2.0, retrait de `HackerOs.AppSdk.FileView` et contrats de fichiers

## Statut

Accepté le 2026-10-19.

- **Décision de Benoit :** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  validé le 2026-10-19, question Q5 (« SDK 2.0 maintenant : `FileView` est
  retiré ; les apps 1.x sont refusées et doivent être recompilées »).
- **Amende :** l'ADR 0114 (gel du SDK 1.0) : la liste des paquets couverts perd
  `.FileView` ; la fixture de compatibilité 1.0 devient une preuve de refus.
- **Reprend :** les ajouts en attente pour la 1.3, `PublishAsync` (ADR 0145)
  et `HackCrossOriginIsolation` (ADR 0146), qui partent dans la 2.0.

## Contexte

Le SDK 1.x promet qu'une app compilée contre `1.x` tourne sur tout OS `1.y`
(ADR 0114 §2). `HackerOs.AppSdk.FileView` en fait partie depuis le gel. Le
retirer au profit du contrat `hackeros.file-view` (ADR 0148) est une rupture,
donc une majeure. L'OS refuse déjà une app d'une autre majeure (ADR 0098 §3).

## Décision

### 1. Version

`build/Versions.props` : `HackerOsVersion` = `2.0.0`, publiée par le
chantier 6 du plan, une fois les chantiers 1 à 5 fusionnés. Jusque-là, les
ajouts restent dans `PublicAPI.Unshipped.txt` et les retraits y sont écrits
`*REMOVED*` (ADR 0114 §1).

### 2. Retiré du SDK

- Le paquet `HackerOs.AppSdk.FileView` entier : `FileView`, `FileViewTree`,
  `FileViewDetails`, `FileViewIcons`, `FileViewBreadcrumb`,
  `FileViewDropZone`, `FileViewItem`, les menus
  (`IFileViewContextMenuProvider` et ses types), `IShellIconProvider`,
  `ShellIcon`, `ShellIconDescriptor`, et `FileTransfer` sous sa forme de
  classe.
- Ses types publics sont écrits `*REMOVED*`. Le code devient
  l'**implémentation interne** de l'explorateur de fichiers
  (`Apps/System/HackerOs.Apps.FileExplorer`), qui n'est pas couvert par la
  promesse (ADR 0114 §2).
- La 2.0 ne garde aucun type `[Obsolete]` pour la transition : comme pour la
  1.0, une app recompile contre la nouvelle surface.

### 3. Ajouté au SDK

| Type | Rôle | ADR |
|---|---|---|
| `IHackFileView`, `HackFileView`, `HackFileViewOptions`, événements | Contrat `hackeros.file-view` | 0148 |
| `IHackFileOperations` | Copie, déplacement, mise à la corbeille, import, conflits de noms à la Nautilus ; mis en œuvre par l'explorateur pour que ces règles ne vivent qu'à un endroit | 0148 |
| `IHackContextMenu`, `HackContextMenu`, `HackMenuItem`, `HackMenuGroup`, `Menus.Attach`, `Menus.Define` | Menu contextuel de l'OS | 0150 |
| `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext`, `FileMenuScope`, `MenuSelection`, `HackFileMenuItems` | Entrées de menu inscrites par les apps | 0151 |
| `HackIcons` (`FromImage`, `Vector`) | Description d'une icône, sans rendu | 0150, 0151 |

Les types `IHackFileOperations` et `HackIcons` remplacent `FileTransfer` et
`ShellIcon` pour les apps. `IHackFileOperations` passe par le contrat
`hackeros.file-dialogs` (ADR 0148 §2), qui porte aussi le dialogue de
conflit de noms.

### 4. Paquets couverts

Le tableau de l'ADR 0114 §2 devient, pour la 2.0 : `HackerOs.App.Abstractions`,
`HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`,
`HackerOs.AppSdk`, `.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.Icons`,
`.VirtualKeyboard`, `HackerOs.Theming.Blazor`, le contrat de build et le format
du manifeste. Le reste de l'ADR 0114 ne change pas.

### 5. Fixtures et preuve

- `Tests/fixtures/sdk-compat` (app 1.0.0, `.nupkg` committé) reste telle
  quelle et devient le test du **refus** d'une app 1.x par le catalogue
  (majeure différente).
- Une nouvelle fixture 2.0.0 est produite par le même `produce-fixture.sh`
  après la publication, committée et jamais reconstruite ; elle utilise
  `HackFileView` et inscrit une entrée de menu, pour que la promesse 2.x couvre
  les nouveaux contrats.

### 6. Migration

- `CodeEditor` (explorateur latéral) passe à `HackFileView`.
- Une app 1.x d'un autre dépôt remplace `FileView` par `HackFileView`,
  `IFileViewContextMenuProvider` par `ContextMenuOpening` du contrôleur ou par
  une entrée inscrite, `FileTransfer` par `IHackFileOperations`, puis se
  recompile.
- [`sdk-packages.md`](../../framework/sdk-packages.md) gagne une section « SDK 2.0 » avec
  la liste des ruptures et ce tableau de migration ;
  [`sdk-reference.md`](../../framework/sdk-reference.md) et
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md) suivent.

## Conséquences

- Toute app 1.x installée est refusée au prochain démarrage de l'OS 2.0 et
  affichée comme incompatible ; ses fichiers ne sont pas touchés.
- Les apps de base sont recompilées avec l'OS, rien ne change pour elles.
- `PublishAsync` et `HackCrossOriginIsolation` sont publiés en 2.0.0 et non
  en 1.3.0 ; aucune 1.3 n'est publiée.

## Références

- ADR 0037, 0098, 0114, 0132, 0145, 0146, 0148, 0150, 0151.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §9,
  [`sdk-packages.md`](../../framework/sdk-packages.md).
