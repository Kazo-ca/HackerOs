> [English version](../../../../en/wasm/dev/adr/0150-os-context-menu.md)

# ADR 0150 : menu contextuel central de l'OS (`hackeros.context-menu`)

## Statut

Accepté le 2026-10-19.

- **Décision de Benoit :** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  validé le 2026-10-19, questions Q6 (fournisseur : l'OS), Q10 (sous-menus),
  Q14 (`@attributes="Menus.Attach(name, item)"` sur n'importe quel élément, et
  déclaration en C#), Q20 (appui long au doigt sur mobile) ; défauts du
  récapitulatif validé : repli des séparateurs, Touche Menu et Maj+F10.
- **Remplace :** pour le contrôle de fichiers, le menu à un niveau de
  l'ADR 0037 §7 (`IFileViewContextMenuProvider`, `MudMenu`), retiré par
  l'ADR 0149.

## Contexte

Il n'existe qu'un menu contextuel, propre à `FileView`, à un seul niveau, rendu
par `MudMenu`. Une app qui veut un clic droit sur une ligne de tableau ou une
carte doit tout refaire. Benoit veut un menu unique, fourni par l'OS,
multi-niveaux, qu'une app attache à n'importe quel élément et qui serve aussi
de base aux menus de fichiers extensibles (ADR 0151).

## Décision

### 1. Fournisseur

La plateforme (`Platform.Blazor`) fournit le contrat `hackeros.context-menu`
(ADR 0148 §3.2) : un seul rendu du menu pour tout l'OS, au thème de l'OS. Le
SDK porte `IHackContextMenu`, `HackContextMenu`, `HackMenuItem`,
`HackMenuGroup`, `Menus` et `HackIcons`.

### 2. Modèle

- **Entrée** : `Id` (unique dans le menu), `Title` (texte déjà traduit par
  l'app), `Icon` (`HackIcons.Vector(bibliothèque, nom)` ou
  `HackIcons.FromImage(asset)`), `IsDisabled`, `IsVisible`, `Shortcut`
  (texte affiché seulement), `Children`, action.
- **Action** : un callback C# de l'instance (`OnClick`, qui reçoit l'objet
  attaché, §4), ou un intent pour une entrée inscrite (ADR 0151). Une entrée
  qui a des enfants n'a pas d'action : elle ouvre son sous-menu.
- **Profondeur** : 3 niveaux au plus (menu, sous-menu, sous-sous-menu). Un
  quatrième niveau est refusé à la construction (exception
  `InvalidOperationException` en C#, erreur de rendu dans le markup).
- **Groupes** : les entrées sont rangées en groupes ordonnés ; un séparateur
  sépare deux groupes. Une app n'écrit jamais de séparateur : elle crée un
  groupe (`HackMenuGroup`, `builder.Group(…)`). Les entrées hors groupe
  explicite forment le groupe implicite où elles apparaissent.

### 3. Séparateurs

Au moment d'ouvrir le menu, après les filtres (`IsVisible`, portée, types) :

1. un groupe dont aucune entrée n'est visible disparaît ;
2. un séparateur n'est rendu qu'**entre deux groupes visibles** ;
3. le menu ne commence ni ne finit par un séparateur, et n'en contient jamais
   deux à la suite.

La même règle s'applique dans chaque sous-menu. Un sous-menu sans enfant
visible masque son entrée parente.

### 4. Dans une app

Déclaration dans le markup :

```razor
<HackContextMenu Name="files">
    <HackMenuItem Id="preview" Title="@L["Menu.Preview"]" OnClick="item => PreviewAsync((Row)item)" />
    <HackMenuGroup>
        <HackMenuItem Id="export" Title="@L["Menu.Export"]">
            <HackMenuItem Id="csv" Title="CSV" OnClick="item => ExportAsync((Row)item, "csv")" />
        </HackMenuItem>
    </HackMenuGroup>
</HackContextMenu>

@foreach (Row row in _rows)
{
    <tr @attributes="Menus.Attach("files", row)">…</tr>
}
```

Ou en C# : `Menus.Define("files", builder => builder.Item(…).Group(g => …))`,
avec le même modèle. Une définition C# et une définition markup du même nom
dans la même instance sont une erreur.

- `Menus` est un service de l'instance (propriété de `HackComponentBase`).
- `Menus.Attach(name, objet)` renvoie les attributs à poser
  (`data-hack-menu`, un jeton opaque). L'objet reste côté C#, dans une table
  de l'instance indexée par jeton ; l'entrée est retirée quand l'élément
  quitte le rendu ou que la fenêtre se ferme. L'objet ne passe jamais par
  JavaScript.
- Avant l'ouverture, l'événement `Opening` du menu reçoit l'objet et peut
  masquer, désactiver ou ajouter des entrées pour cet objet.
- Les callbacks ne servent qu'au menu de l'instance qui les déclare.

### 5. Ouverture : le script de la plateforme

Un petit module JavaScript de `Platform.Blazor` (`wwwroot`, importé par le
C#, jamais référencé par un hôte) écoute, sur le document :

- `contextmenu` (clic droit) ;
- l'**appui long** au doigt (500 ms sans bouger de plus de 10 px, comme
  Android) ;
- la **Touche Menu** et **Maj+F10** sur l'élément qui a le focus.

Il trouve le plus proche ancêtre `data-hack-menu`, empêche le menu du
navigateur et rend au C# le jeton et la position (le coin de l'élément pour le
clavier). Sans attribut, le menu du navigateur n'est pas empêché, sauf dans
les zones de l'OS qui ont leur propre menu (bureau, barre des tâches).

### 6. Rendu

- Composant natif de la plateforme avec CSS isolé (pas `MudMenu`), au thème
  de l'OS (jetons de couleur, police, ombre).
- Positionné pour rester à l'écran : il s'ouvre vers la gauche ou vers le haut
  si la place manque ; un sous-menu s'ouvre du côté libre.
- Clavier : flèches haut et bas (en boucle, en sautant les entrées
  désactivées), flèche droite ou Entrée pour ouvrir un sous-menu, flèche
  gauche pour le fermer, Entrée pour choisir, Échap pour fermer un niveau,
  première lettre pour aller à l'entrée. Le focus revient à l'élément
  d'origine à la fermeture.
- ARIA : `role="menu"`, `menuitem`, `separator`, `aria-haspopup`,
  `aria-expanded` et `aria-disabled` écrits en texte `"true"`/`"false"`
  (common-pitfalls §8).
- Un clic hors du menu, la perte du focus de la fenêtre ou un défilement le
  ferment. Un seul menu est ouvert à la fois dans tout l'OS.
- Mobile : le même menu, en feuille du bas plein largeur ; un sous-menu
  remplace le niveau courant avec une entrée « Retour ».

## Conséquences

- Toute app a un clic droit cohérent avec l'OS sans code JavaScript.
- Le contrôle de fichiers (ADR 0149) construit ses menus par défaut sur ce
  modèle ; les entrées inscrites (ADR 0151) s'y ajoutent.
- Le chantier 2 du plan livre le contrat, le rendu, `Menus.Attach`, le script,
  le clavier et l'appui long, avec des tests du repli des séparateurs.

## Alternatives écartées

- **`MudMenu`** : pas de sous-menus à trois niveaux maîtrisés, ni de rendu
  commun au bureau et aux fenêtres ; l'ADR 0016 réserve MudBlazor aux surfaces
  complexes d'une app, pas au shell.
- **Un menu par app** : c'est l'existant ; il ne permet pas les entrées
  inscrites par d'autres apps.
- **Sérialiser l'objet attaché vers JavaScript** : coût, fuite de données et
  objets non sérialisables ; le jeton suffit.

## Références

- ADR 0016, 0037, 0148, 0149, 0151.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §6,
  [`old/common-pitfalls.md`](../old/common-pitfalls.md).
