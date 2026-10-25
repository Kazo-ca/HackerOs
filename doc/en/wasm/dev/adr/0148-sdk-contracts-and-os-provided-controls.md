# ADR 0148 : le SDK ne porte que des contrats ; l'OS fournit les contrôles et dialogues partagés

## Statut

Accepté le 2026-10-19.

- **Décision de Benoit :** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  validé le 2026-10-19, question Q3 (« le SDK fournit l'interface, pas
  l'implémentation ; l'explorateur s'inscrit au démarrage comme fournisseur du
  contrôle et de ses boîtes de dialogue ; l'OS sert de proxy ») et Q6 (le menu
  générique est fourni par l'OS).
- **Complète :** l'ADR 0063 (composants intégrables) : son mécanisme devient la
  base de tous les contrôles partagés.
- **Remplace :** l'ADR 0037 §1 (bibliothèque `HackerOs.AppSdk.FileView`
  compilée dans l'app hôte) et §8 (l'explorateur garde ses propres boîtes
  « Ouvrir avec » et « Propriétés »). Les autres points de l'ADR 0037 (modèle
  unique des trois vues, renommage en place, glisser-déposer, icônes déléguées,
  événements annulables) restent vrais pour l'implémentation de l'explorateur.
- **Fournisseurs admis :** apps système seulement (défaut retenu, question
  posée à Benoit sur une carte le 2026-10-19 dans le fil « Chantier 0 » ; si la
  réponse diffère, un ADR suivant remplace le §3.3).

## Contexte

`FileView` est aujourd'hui une bibliothèque du SDK compilée dans chaque app
qui l'utilise (explorateur, éditeur de code). Une correction de l'OS n'atteint
une app qu'après sa recompilation, et 200 apps embarqueraient 200 copies du
même code. Le terminal (ADR 0063) montre l'autre voie : le SDK donne un
contrat, une app s'inscrit comme fournisseur dans son manifeste, l'OS charge
le fournisseur au premier usage et le rend dans la fenêtre de l'hôte.

## Décision

### 1. Le SDK ne contient que des contrats

Pour un contrôle ou un dialogue partagé, le SDK ne livre que :

- le **nom du contrat** (`HackComponentContracts.FileView`…) ;
- le **contrôleur** (interface, par exemple `IHackFileView`) ;
- l'enregistrement d'**options** et les types d'événements ;
- un **composant hôte typé** (`HackFileView`, `HackContextMenu`) avec des
  paramètres et des `EventCallback` ordinaires, construit sur
  `HackEmbeddedComponent<T>` (ADR 0063 §3).

Aucune implémentation visible (vue, menu, dialogue) n'est compilée dans une
app. Le composant hôte reçoit à l'exécution le `RenderFragment` du
fournisseur inscrit et son contrôleur.

### 2. Les contrats de cette série

| Contrat | Contrôleur et composant du SDK | Fournisseur |
|---|---|---|
| `hackeros.file-view` | `IHackFileView`, `HackFileView` | Explorateur de fichiers |
| `hackeros.file-dialogs` | « Ouvrir avec », « Propriétés », conflit de noms, et la vue de fichiers des dialogues Ouvrir / Enregistrer / Choisir un dossier (`IFileDialogService` reste le point d'entrée) | Explorateur de fichiers |
| `hackeros.context-menu` | `IHackContextMenu`, `HackContextMenu`, `Menus.Attach` (ADR 0150) | Plateforme (`Platform.Blazor`) |

### 3. Inscription et résolution

1. **Une app** s'inscrit comme aujourd'hui, dans `AppDefinition.Components`
   (ADR 0063 §1). L'OS lit les manifestes au démarrage et connaît donc tous
   les fournisseurs ; le code de l'app n'est chargé qu'au premier usage du
   contrat (`AppLifecycleOrchestrator.EnsureDescriptorAsync`).
2. **La plateforme** inscrit ses propres fournisseurs dans le registre au
   démarrage, sans manifeste (`hackeros.context-menu`).
3. **Contrats réservés.** `hackeros.file-view` et `hackeros.file-dialogs`
   n'acceptent que des apps système (ADR 0048) ; `hackeros.context-menu`
   n'accepte que la plateforme. Une app non système qui déclare un contrat
   réservé est refusée par `AppManifestValidator` (diagnostic du générateur) et
   ignorée par l'OS. `hackeros.terminal` reste ouvert à toute app, comme
   l'ADR 0063 le prévoit.
4. Le registre garde l'ordre de l'ADR 0063 §3 (apps système d'abord, puis par
   identifiant) ; un fournisseur qui ne se charge pas laisse la place au
   suivant.

### 4. Les droits sont ceux de l'hôte

Le rendu fourni s'exécute dans le processus de l'**app hôte**, avec ses
passerelles (`HostContext`) et ses capacités, jamais celles du fournisseur
(principe de l'ADR 0037 §1, gardé). Une app ne voit pas un fichier de plus en
affichant le contrôle de l'explorateur. Un dialogue fourni reçoit lui aussi
les passerelles de l'app qui l'ouvre (ou la poignée choisie, pour Ouvrir /
Enregistrer).

### 5. Fournisseur indisponible

Sans fournisseur (app désactivée, chargement en échec, hôte de test), le
composant affiche son fragment `Unavailable` ou la raison. Comme les
fournisseurs des contrats réservés sont système, ce cas est une **erreur**
écrite au journal système (`/var/log/syslog`), jamais un état normal.

### 6. Dialogues

`IDialogService` et `IFileDialogService` gardent leur forme dans le SDK.
L'OS sert de proxy : il ouvre le dialogue de `hackeros.file-dialogs` dans une
fenêtre modale de l'app appelante. Ainsi « Ouvrir avec » et « Propriétés »
sont les mêmes partout (explorateur, bureau, toute app).

## Conséquences

- Une correction du contrôle de fichiers, des dialogues ou du menu atteint
  toutes les apps sans recompilation.
- L'explorateur devient l'unique source de vérité du contrôle de fichiers :
  il le fournit et l'affiche lui-même par `HackFileView`.
- La bibliothèque `HackerOs.AppSdk.FileView` quitte le SDK (ADR 0149).
- Le chantier 1 du plan livre le composant hôte typé, le registre lu au
  démarrage, les contrats réservés et le passage des droits de l'hôte.

## Alternatives écartées

- **Garder `FileView` dans le SDK** : chaque correction impose de recompiler
  toutes les apps.
- **Un service DI global par contrôle** : il faudrait charger l'explorateur au
  démarrage ; le manifeste garde le chargement paresseux (ADR 0063).

## Références

- ADR 0037, 0048, 0063, 0149, 0150.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §4,
  [`embedded-components.md`](../../framework/embedded-components.md),
  [`file-explorer.md`](../file-explorer.md).
