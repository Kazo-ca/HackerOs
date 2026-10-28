> [English version](../../../en/wasm/dev/window-runtime-displays.md)

# Écrans dans le gestionnaire de fenêtres

Chantier `multi-monitor/1-window-runtime-displays` du
[plan multi-moniteur](plans/multi-monitor-plan.md), mise en œuvre de
l'[ADR 0178 §2](adr/0178-extended-screens-in-display-surfaces.md). Ce chantier ne
change rien à l'écran : sans écran secondaire, l'OS se comporte comme avant. Les
chantiers suivants (portail des fenêtres, service d'écrans) s'appuient sur lui.

## 1. Identité d'un écran

`DisplayId` (`HackerOs.Windowing.Abstractions`) est le numéro que voit
l'utilisateur :

| Membre | Rôle |
| --- | --- |
| `DisplayId.Primary` | L'écran 1, l'onglet principal. C'est aussi la valeur par défaut (`default(DisplayId)`). |
| `DisplayId.FromNumber(n)` | L'écran `n` (1 ou plus). |
| `Number`, `IsPrimary` | Numéro affiché, écran 1 ou non. |

`WindowRuntimeState.DisplayId` dit sur quel écran est la fenêtre (par défaut
l'écran 1). `Bounds` et `RestoreBounds` sont dans les coordonnées de cet écran.

## 2. Une zone de travail par écran

`WindowRuntime` garde une zone de travail par écran enregistré :

| Membre | Rôle |
| --- | --- |
| `WorkArea` | Zone de l'écran 1, comme avant. |
| `Displays` | Écrans enregistrés, écran 1 d'abord, puis par numéro. |
| `GetWorkArea(DisplayId)` | Zone d'un écran ; `KeyNotFoundException` s'il n'existe pas. |
| `NextDisplayId` | Le plus petit numéro libre, à donner au prochain écran (ADR 0178 §5). |

Déplacer, redimensionner, agrandir et contraindre une fenêtre se font dans la zone
de **son** écran. Il y a toujours un seul focus et un seul ordre Z pour tout l'OS.
`WindowKeyboardCommands` (ancrage, menu de fenêtre) et `WindowGeometryRecorder`
(détection de l'ancrage) lisent aussi la zone de l'écran de la fenêtre.

## 3. Commandes et événements

| Commande | Effet | Événements |
| --- | --- | --- |
| `ChangeViewportCommand(zone)` | Nouvelle zone de l'écran 1 (code existant inchangé). | `WindowViewportChangedEvent` (`DisplayId` = écran 1) |
| `ChangeViewportCommand(zone, écran)` | Nouvelle zone d'un écran ; seules ses fenêtres sont contraintes. | `WindowViewportChangedEvent` (`DisplayId` = écran) |
| `AddDisplayCommand(écran, zone)` | Enregistre un écran vide. Déjà enregistré : `InvalidOperationException`. | `DisplayAddedEvent` |
| `MoveWindowToDisplayCommand(fenêtre, écran)` | Change la fenêtre d'écran, avec sa famille modale. Même écran : aucun événement. Écran inconnu : `KeyNotFoundException`. | un `WindowDisplayChangedEvent` par fenêtre déplacée |
| `RemoveDisplayCommand(écran)` | Ramène ses fenêtres sur l'écran 1, puis retire l'écran. Écran 1 : `InvalidOperationException` ; écran inconnu : aucun événement. | `WindowDisplayChangedEvent` par fenêtre, puis `DisplayRemovedEvent` |

`WindowDisplayChangedEvent` donne l'écran quitté, le nouvel écran, les nouvelles
bornes et la géométrie de restauration. `WindowAppEventDispatcher` s'en sert pour
appeler `OnWindowResized` quand la taille de la zone client change.

## 4. Changer d'écran garde l'état

| État de la fenêtre | Sur l'écran cible |
| --- | --- |
| Agrandie | Reste agrandie et remplit la zone cible. Sa géométrie de restauration garde sa position relative. |
| Ancrée (moitié gauche ou droite exacte de sa zone) | Reste ancrée du même côté, sur la moitié de la zone cible. |
| Normale | Garde sa taille et sa position relative au coin haut-gauche, en proportion de la taille des zones, puis elle est ramenée dans la zone cible (le minimum déclaré l'emporte, ADR 0095 §2). |
| Réduite | Change d'écran et reste réduite. Si elle a été réduite depuis l'état agrandi, elle remplit la zone cible. |

Le focus et l'ordre Z ne changent pas.

L'ADR 0177 (fil « Corriger les boutons des fenêtres maximisées ») ajoute une mémoire
de la taille d'avant l'ancrage. Quand il arrive dans la série, cette mémoire suit la
fenêtre comme sa géométrie de restauration.

## 5. Fenêtres modales

- Une fenêtre modale (`OwnerModal`) s'ouvre sur l'écran de son propriétaire, quel
  que soit l'écran demandé.
- Déplacer une fenêtre déplace toute sa famille modale : son propriétaire (en
  remontant les liens modaux) et toutes les fenêtres modales qui en descendent. Les
  événements suivent l'ordre Z, de l'arrière vers l'avant.

## 6. Écran retiré

Quand un écran disparaît (surface fermée, perdue, moniteur débranché), le service
d'écrans (chantier 3) envoie `RemoveDisplayCommand`. Chaque fenêtre de l'écran revient
sur l'écran 1 avec les règles du §4, comme le contenu d'une surface revient sous
son ancre. Un écran retiré libère son numéro.

## 7. API publique et tests

- Les nouveaux types et membres sont dans `PublicAPI.Unshipped.txt` de
  `HackerOs.Windowing.Abstractions` et `HackerOs.Windowing.Core`. Les signatures
  existantes ne changent pas (`ChangeViewportCommand` et
  `WindowViewportChangedEvent` gardent leur constructeur et reçoivent un second
  constructeur).
- Tests : `Tests/HackerOs.Windowing.Core.Tests/WindowRuntimeDisplayTests.cs`
  (numéros, zones par écran, changement d'écran dans chaque état, modales, écran
  retiré).
