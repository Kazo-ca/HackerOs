# ADR 0156 : fournisseur du contrôle de fichiers remplaçable par une app tierce

## Statut

Accepté le 2026-10-20.

- **Décision de Benoit :** option « Remplaçable » de la carte « Laisser une
  app tierce fournir le contrôle de fichiers et ses dialogues à la place de
  l'explorateur ? » (2026-10-20, 01:46Z, fil « Chantier 0 »), dont la
  conséquence affichée était : « Une app tierce peut s'inscrire ; Paramètres ▸
  Applications choisit le fournisseur, l'explorateur par défaut. »
- **Qui choisit et où :** défaut retenu « Admin, partout » (§3), question
  posée à Benoit sur une carte le 2026-10-20 dans le même fil. Si la réponse
  diffère, un ADR suivant remplace le §3.
- **Remplace :** l'ADR 0148 §3.3 pour `hackeros.file-view` et
  `hackeros.file-dialogs` ; la seconde puce de l'ADR 0153 §4 (refus
  `HOSAPP0025` à l'agrégation, refus `catalog.component.reserved` à
  l'exécution). `hackeros.context-menu` reste réservé à la plateforme, sans
  changement.

## Contexte

L'ADR 0148 réservait le contrôle de fichiers et ses dialogues aux apps
système, et le chantier 1 l'a mis en œuvre. Benoit veut qu'une app tierce
puisse remplacer l'explorateur comme fournisseur, à la manière d'un
gestionnaire de fichiers de remplacement sous Linux.

Le rendu fourni s'exécute dans le processus de l'app hôte, avec les droits de
cette app (ADR 0148 §4). Un fournisseur tiers exécute donc son code dans
toutes les apps qui affichent le contrôle, y compris des apps qui ont des
capacités dangereuses, comme une extension du shell de Windows chargée dans
`explorer.exe`.

## Décision

### 1. Les deux contrats sont ouverts

Toute app `Window` peut déclarer `hackeros.file-view` et
`hackeros.file-dialogs` dans `AppDefinition.Components` (ADR 0063 §1). Le
générateur, l'agrégation et le catalogue ne la refusent plus ; `HOSAPP0025`
ne s'applique plus à ces deux contrats. `hackeros.context-menu` reste refusé à
toute app (`HOSAPP0009`, ADR 0153 §4).

### 2. Le fournisseur actif est un réglage

- `/etc/hackeros/components.config`, document de la couche système
  ([`settings-files.md`](../../framework/settings-files.md)) :

  ```ini
  [providers]
  hackeros.file-view=com.example.files
  hackeros.file-dialogs=com.example.files
  ```

- Une clé absente vaut **l'explorateur** (`org.hackeros.file-explorer`), ou
  l'ordre de l'ADR 0063 §3 si l'explorateur manque. Une installation neuve n'a
  aucune clé.
- Chaque contrat se choisit séparément ; Paramètres propose de régler les deux
  ensemble quand l'app fournit les deux.
- Le registre (`AppCatalog.GetComponentProviders`, ADR 0153 §3) met le
  fournisseur choisi en tête ; les autres restent des replis.

### 3. Qui choisit : un administrateur, pour toute la machine

- Paramètres ▸ Applications ▸ « Composants de l'OS » liste, pour chaque
  contrat, les apps qui le fournissent, et le fournisseur actif.
- Choisir une app non système demande un **administrateur** (comme toute
  écriture dans `/etc`) et passe par une **boîte d'approbation** du modèle des
  capacités dangereuses (ADR 0049), avec la phrase de risque : « Le code de
  cette app s'exécutera dans toutes les applications qui affichent des
  fichiers, avec leurs droits. » La décision est journalisée dans
  `/var/log/auth.log`.
- Revenir à l'explorateur ne demande pas d'approbation, seulement un
  administrateur.
- Le fournisseur choisi s'applique **partout** : bureau, explorateur,
  Paramètres, éditeur de code, dialogues, toute app.

### 4. Repli

- Si le fournisseur choisi est désinstallé, l'OS retire sa ligne.
- S'il est désactivé (ADR 0048), ne se charge pas, ou ne dérive pas de
  `HackEmbeddableComponent`, l'OS prend le suivant (l'explorateur en premier)
  et écrit `component.fallback` en avertissement dans `/var/log/syslog`
  (source `components`). La ligne est gardée : le fournisseur revient quand il
  se charge de nouveau.
- L'erreur `component.unavailable` (ADR 0153 §6) reste réservée au cas où
  **aucun** fournisseur ne se charge.

### 5. Ce qui ne change pas

- Les droits sont toujours ceux de l'hôte (ADR 0148 §4) : un fournisseur tiers
  ne voit pas un fichier de plus que l'app qui l'affiche.
- Un fournisseur tiers vise la même majeure du SDK que l'OS ; les paramètres
  qu'il ne connaît pas sont ignorés (ADR 0153 §2).
- Les entrées de menu inscrites (ADR 0151) et le menu de l'OS (ADR 0150)
  s'appliquent quel que soit le fournisseur : le contrat impose au
  fournisseur de construire ses menus sur `hackeros.context-menu`, avec les
  identifiants de `HackFileMenuItems`.

## Conséquences

- Le code du chantier 1 change : retrait du refus pour ces deux contrats,
  lecture de `components.config`, fournisseur choisi en tête, repli
  journalisé. Paramètres gagne la page « Composants de l'OS ». Ce travail est
  un chantier à part, placé après le chantier 3 (qui écrit le fournisseur de
  l'explorateur) pour ne pas toucher les mêmes fichiers en même temps.
- [`settings-files.md`](../../framework/settings-files.md) gagne `components.config` ;
  [`embedded-components.md`](../../framework/embedded-components.md) décrit le choix du
  fournisseur.
- Tests attendus : une app tierce qui déclare les deux contrats passe
  l'agrégation ; le réglage la met en tête ; désinstallée, la ligne disparaît ;
  en échec de chargement, l'explorateur prend le relais et le journal le dit ;
  `hackeros.context-menu` reste refusé.

## Références

- ADR 0048, 0049, 0063, 0148, 0150, 0151, 0153.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §4.
