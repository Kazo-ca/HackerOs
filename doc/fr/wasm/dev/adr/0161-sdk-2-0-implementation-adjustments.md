> [English version](../../../../en/wasm/dev/adr/0161-sdk-2-0-implementation-adjustments.md)

# ADR 0161 : SDK 2.0, ajustements d'implémentation

## Statut

Accepté le 2026-10-20.

- **Chantier :** `desk/6-sdk-2` (plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §9
  et §12, chantier 6).
- **Ajuste :** l'ADR 0149 (§2 retrait, §3 ajouts, §5 fixtures). Ses décisions
  restent vraies : `HackerOs.AppSdk.FileView` quitte le SDK, la version passe à
  `2.0.0`, une app 1.x est refusée. Cet ADR fixe ce qu'elles laissaient ouvert.
- **Question posée à Benoit** (2026-10-20, fil « Desk 6 ») : où vivent les
  règles de copie et de déplacement (§2). Réponse de Benoit le 2026-10-20 :
  « Règles dans le SDK ».

## Contexte

L'ADR 0149 décide le retrait de la bibliothèque `FileView`, l'arrivée des
contrats de fichiers et la publication de la 2.0. En l'écrivant, il fallait
fixer : où va le code retiré, ce que devient `FileTransfer` (que des apps
appelaient hors du contrôle), comment se prouve le refus des apps 1.x, et ce
que porte la nouvelle fixture.

## Décision

### 1. Le code de `FileView` entre dans l'assemblage de l'explorateur

- Les sources de `Shared/HackerOs.AppSdk.FileView` vont dans
  `Apps/System/HackerOs.Apps.FileExplorer/FileControl/`, espace de noms
  `HackerOs.Apps.FileExplorer.FileControl` (un espace `…FileExplorer.FileView`
  masquerait le type `FileView` dans le code de l'explorateur). Le projet et le paquet
  `HackerOs.AppSdk.FileView` disparaissent ; ses fichiers `PublicAPI` partent
  avec lui (pas de lignes `*REMOVED*` : il n'y a plus de bibliothèque où les
  écrire). La rupture est listée dans
  [`sdk-packages.md`](../../framework/sdk-packages.md#sdk-20).
- Les composants Razor restent `public` (le générateur Razor les déclare
  ainsi) et figurent dans le `PublicAPI` de l'explorateur, une app de base que
  la promesse ne couvre pas (ADR 0114 §2). Une app ne peut pas les atteindre :
  l'explorateur est un assemblage chargé à la demande que les apps ne
  référencent pas.
- Les scripts collocalisés sont servis sous
  `_content/HackerOs.Apps.FileExplorer/FileControl/…` ; les textes passent dans
  `Resources/FileControl/Strings*.resx` de l'explorateur, et ceux des transferts
  dans les `Strings*.resx` de `HackerOs.AppSdk`.
- Devenus du code d'app (sous `Apps/`), ses composants suivent la règle du cadre
  ambiant (ADR 0066, 0067) : ils dérivent de `HackComponentBase` et le rappel
  JavaScript `OnPasteShortcutAsync` entre dans le cadre de l'app. Le temps de
  la vue est donc compté à l'app qui l'affiche, plus au compositeur.
- L'hôte (`HackerOs.Ecosystem.Host`) n'enregistre plus `IShellIconProvider` :
  la vue prend `DefaultShellIconProvider` quand aucun n'est fourni. L'OS ne
  référence donc plus le code de la vue avant le chargement de l'explorateur.
- Les tests `HackerOs.AppSdk.FileView.Tests` deviennent
  `HackerOs.Apps.FileExplorer.FileControl.Tests` ; ceux de `FileTransfer`
  deviennent `HackFileOperationsTests` (`HackerOs.AppSdk.Tests`).

### 2. Les règles de copie restent dans le SDK : `HackFileOperations`

L'ADR 0149 §3 prévoyait un contrat `IHackFileOperations` mis en œuvre par
l'explorateur. Le code montre que `FileTransfer` n'est que de la logique sur
l'API `Hack*`, exécutée avec les droits de l'app appelante ; sa seule partie
visible, le dialogue « Remplacer / Garder les deux / Ignorer », passe déjà par
`hackeros.file-dialogs` (`IDialogService.ResolveFileConflictAsync`). Placer
les règles chez le fournisseur obligerait tout fournisseur tiers (ADR 0156) à
les réécrire, et les rendrait inaccessibles à une commande dans un worker.

- `HackFileOperations` (statique, `HackerOs.AppSdk.IO`, à côté de `HackFile`
  et `HackDirectory`) porte `CopyAsync`, `MoveAsync`, `TransferAsync`,
  `ImportAsync`, `MoveToTrashAsync` et `GetCopyName`, avec les types
  `HackFileTransferMode`, `HackFileConflictChoice`,
  `HackFileConflictResolution`, `HackFileTransferConflict`,
  `HackFileConflictResolver`, `HackFileTransferResult`,
  `HackFileTransferItemResult`, `HackFileTransferFailure` et `HackHostFile`.
- `IDialogService.AsFileConflictResolver(owner)` (`HackerOs.AppSdk.Blazor`)
  donne le résolveur qui pose la question par le fournisseur de
  `hackeros.file-dialogs`.
- L'explorateur, le bureau et tout fournisseur appellent ces règles ; elles ne
  vivent qu'à un endroit, et la promesse 2.x les couvre.
- Il n'y a pas de type `IHackFileOperations`.

### 3. Refus des apps 1.x et fixtures

- Les fixtures `Contoso.Sdk1Compat.1.0.0` et `Contoso.Sdk11Compat.1.1.0` restent
  telles quelles. Les tests de compatibilité vérifient désormais que le
  catalogue les **refuse** (`catalog.sdk.incompatible`, majeure différente).
- `Contoso.Sdk2Compat.2.0.0` est produite par `produce-fixture.sh` contre les
  paquets 2.0.0, committée et jamais reconstruite. Sa fenêtre affiche
  `HackFileView`, son manifeste inscrit une entrée de menu de fichiers
  (`[RegisterFileContextMenu]`), sa commande copie un fichier par
  `HackFileOperations`. Les tests vérifient qu'elle est admise, qu'elle se lie
  et que sa commande tourne.
- Le scénario de l'hôte généré par le modèle (`HackerOs.Templates.E2E.Tests`)
  vérifie qu'un hôte qui référence les deux fixtures 1.x ne se construit pas
  (`HOSAPP0021`, la règle du catalogue appliquée au build, ADR 0098 §3), puis
  qu'avec la fixture 2.0 il démarre, montre le contrôle de fichiers dans la
  fenêtre tierce et exécute la commande.

### 4. Publication

Toutes les entrées de `PublicAPI.Unshipped.txt` passent dans
`PublicAPI.Shipped.txt` (`build/tools/ship-public-api.sh`) et
`HackerOsVersion` vaut `2.0.0`. Les manifestes générés portent
`sdkCompatibility.minimumVersion` `2.0.0`.

## Conséquences

- Un hôte qui référence une app tierce 1.x ne se construit plus, et un OS 2.0
  qui en trouve une à son catalogue l'affiche comme incompatible ; elle doit
  être recompilée contre les paquets 2.0.0.
- Une correction des règles de copie touche le SDK, donc suit le SemVer.
- Le code de la vue ne se charge qu'avec l'explorateur (assemblage à la
  demande) : le téléchargement initial de l'OS diminue d'autant.

## Références

- ADR 0058, 0098, 0114, 0148, 0149, 0155, 0156, 0157.
- [`sdk-packages.md`](../../framework/sdk-packages.md),
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md).
