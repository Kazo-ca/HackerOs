> [English version](../../../../en/wasm/dev/adr/0153-contract-host-implementation-adjustments.md)

# ADR 0153 : ajustements de l'hôte de contrats générique

## Statut

Accepté le 2026-10-19.

- **Chantier :** `desk/1-contract-host` (chantier 1 du plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §11).
- **Ajuste :** l'ADR 0148 §1, §3 et §5 sur des points de mise en œuvre ; ses
  décisions restent vraies.

## Contexte

L'ADR 0148 décide que le SDK ne porte que des contrats et que l'OS fournit à
l'exécution le contrôle de fichiers, ses dialogues et le menu contextuel. Le
chantier 1 livre la mécanique commune ; quelques détails n'étaient pas fixés
par l'ADR ou ne pouvaient pas l'être tels quels.

## Décision

### 1. Le composant hôte typé : `HackContractComponent<TController>`

Le SDK Blazor gagne une classe de base abstraite
`HackContractComponent<TController>` (`HackerOs.AppSdk.Blazor.Components`).
Un composant typé (`HackFileView`, `HackContextMenu`) en dérive, déclare ses
`[Parameter]` ordinaires et les recopie par nom dans
`AddProviderParameters`. Il se rend par `HackEmbeddedComponent<T>`, qui gagne
le paramètre `Parameters` (dictionnaire transmis par nom au fournisseur, à
côté d'`Options` et `ControllerReady`, qui gardent la priorité). La base
expose `ControllerChanged`, `Unavailable`, `Controller` et le crochet
`OnControllerChangedAsync`. Un nouveau dictionnaire n'est transmis que si une
valeur a changé (comparaison superficielle), pour ne pas redessiner le
fournisseur à chaque rendu de l'hôte.

`HackEmbeddedComponent<T>` garde son API 1.x : la base est une composition,
pas un parent, afin que rien ne quitte `PublicAPI.Shipped.txt`.

### 2. Tolérance de version des paramètres

`HackEmbeddableComponent` capture les paramètres qu'il ne déclare pas
(`UnmatchedParameters`, `CaptureUnmatchedValues`). Une app compilée avec un
SDK plus récent peut donc passer un paramètre que le fournisseur de l'OS ne
connaît pas encore : il est ignoré au lieu de faire échouer le rendu, comme
une bibliothèque plus ancienne ignore un argument facultatif. Un fournisseur
ne déclare pas son propre `CaptureUnmatchedValues` (Blazor n'en permet qu'un).

### 3. Registre lu au démarrage dans le catalogue

Le registre des fournisseurs est construit avec le catalogue
(`AppCatalog.ComponentProviders`, `GetComponentProviders(contract)`), donc à
partir des manifestes lus au démarrage, comme les commandes du `PATH`
(ADR 0098 §4). L'ordre est celui de l'ADR 0063 §3. L'état installé est
appliqué à la résolution par `HackComponentHost`.

### 4. Contrats réservés : où le refus a lieu

Les noms vivent dans `HackerOs.App.Abstractions`
(`AppComponentContracts`, `AppComponentProviderScope`) pour que le
validateur les connaisse ; le SDK les répète dans `HackComponentContracts`.

- **`hackeros.context-menu` (plateforme)** : `AppManifestValidator.Validate`
  refuse toute app qui le déclare (`manifest.component.contract.platform`) ;
  le générateur l'arrête donc au build de l'app (`HOSAPP0009`).
- **`hackeros.file-view`, `hackeros.file-dialogs` (apps système)** : le
  manifeste seul ne dit pas si une app est système (seule la
  `ProjectReference` de l'hôte le dit, ADR 0048). Le refus a donc lieu à
  l'**agrégation** de l'hôte, par
  `AppManifestValidator.ValidateComponentProviders`, avec le nouveau code
  **`HOSAPP0025`** (comme `HOSAPP0024` pour `oomScoreAdj`). À l'exécution, le
  catalogue écarte l'inscription et écrit `catalog.component.reserved` dans
  `/var/log/syslog` ; l'app reste installable.

### 5. Fournisseurs de la plateforme

La plateforme inscrit ses fournisseurs dans le conteneur de services
(`HackPlatformComponent(contract, type)`), que `HackComponentHost` reçoit au
démarrage. Ils sont essayés avant toute app et résolus sans charger
d'assembly ; leur `ProviderAppId` vaut
`HackComponentResolution.PlatformProviderId` (`"platform"`, jamais un
identifiant d'app valide).

### 6. Erreur au journal système

Quand un contrat réservé n'a aucun fournisseur qui se charge,
`HackComponentHost` écrit `component.unavailable` en erreur dans
`/var/log/syslog` (source `components`). Un contrat ouvert sans fournisseur
(aucun terminal installé) reste un état normal, seulement montré à
l'utilisateur.

### 7. Droits de l'hôte

Rien ne change dans le mécanisme : le fournisseur est rendu dans la fenêtre de
l'hôte, reçoit l'`AppContext` en cascade de l'hôte, et les callbacks qu'il
invoque s'exécutent dans le contexte ambiant de l'hôte (`HackComponentBase`).
Un test le vérifie pour le composant typé.

## Conséquences

- Les chantiers 2 et 3 n'ont qu'à écrire leur contrôleur, leur composant typé
  et leur fournisseur.
- Nouvelles API non livrées (`PublicAPI.Unshipped.txt`) dans
  `App.Abstractions`, `AppSdk`, `AppSdk.Blazor`, `Platform.Core` et
  `Platform.Blazor` ; elles seront publiées avec le SDK 2.0 (chantier 6).

## Références

- ADR 0048, 0063, 0098, 0126, 0148.
- [`embedded-components.md`](../../framework/embedded-components.md),
  [`app-model.md`](../../framework/app-model.md).
