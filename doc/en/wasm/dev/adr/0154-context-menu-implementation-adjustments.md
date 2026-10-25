# ADR 0154 : ajustements du menu contextuel de l'OS

## Statut

Accepté le 2026-10-20.

- **Chantier :** `desk/2-context-menu` (chantier 2 du plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §11).
- **Ajuste :** l'ADR 0150 §1, §4, §5 et §6 sur des points de mise en œuvre ;
  ses décisions restent vraies.

## Contexte

L'ADR 0150 décide un menu unique fourni par la plateforme, multi-niveaux, que
les apps attachent à leurs éléments par `Menus.Attach`. Quelques détails
n'étaient pas fixés ou ne pouvaient pas l'être tels quels une fois le code
écrit sur l'hôte de contrats du chantier 1 (ADR 0153).

## Décision

### 1. Le fournisseur inscrit, la coquille dessine

`HackContextMenu` est un `HackContractComponent<IHackContextMenu>` ; la
plateforme fournit le contrat par `HackPlatformComponent`
(`PlatformContextMenu`). Le fournisseur rendu à la place du menu dans la fenêtre
**ne dessine rien** : il inscrit le menu dans la fenêtre et rend le contrôleur.
Le menu ouvert est dessiné par `ContextMenuOverlay`, un seul par coquille
(`DesktopShell`, `MobileShell`), au-dessus de toutes les fenêtres : c'est ce qui
garantit « un seul menu ouvert dans tout l'OS » et un positionnement dans la
fenêtre du navigateur, pas dans celle de l'app.

Le SDK recueille lui-même les entrées du markup (`HackMenuItem`,
`HackMenuGroup` en cascade) et ne passe au fournisseur qu'un constructeur
(`Build`), le nom, le nom accessible et `Opening`. Un menu absent ne montre
rien à sa place (`HackContractComponent.DefaultUnavailable`, nouveau crochet) ;
l'OS écrit déjà l'erreur au journal (ADR 0153 §6).

### 2. Une portée par fenêtre

`IHackMenuHost`, interface du SDK mise en œuvre par la plateforme
(`ContextMenuScope`), est donnée en cascade par `WindowAppRenderer`, **une par
fenêtre**, détruite avec elle (menus, objets attachés, menu ouvert). L'unicité
du nom d'un menu vaut donc par fenêtre. `Menus` (type `HackMenus`) existe sur
`WindowAppBase` **et** sur `HackComponentBase` : `WindowAppBase` ne dérive pas de
`HackComponentBase`. Hors fenêtre (test unitaire), `Menus.Attach` ne renvoie
aucun attribut et rien ne s'ouvre.

### 3. Jetons et libération

Le même objet attaché à nouveau reçoit le même jeton (par référence pour un
objet, par valeur pour une chaîne ou un type valeur, recréés à chaque rendu) :
redessiner une liste ne fait pas grossir la table. Les jetons des éléments qui
quittent la page sont signalés par le script (observation des retraits, vérifiés
une seconde plus tard pour ne pas oublier un élément redessiné ou déplacé dans
une fenêtre détachée) ; la fermeture de la fenêtre vide tout.

### 4. Le script est celui du composant

Le script de l'ADR 0150 §5 est `ContextMenuOverlay.razor.js`, module propre au
composant (AGENTS.md, « Component-scoped »), importé par le C#, plutôt qu'un
fichier de `wwwroot`. Il respecte un `contextmenu` déjà annulé par une app
(le menu `MudMenu` actuel de `FileView` reste donc intact jusqu'au chantier 3),
laisse le menu du navigateur dans un champ de texte non attaché, et ne traite
que le document du bureau : une fenêtre détachée (ADR 0078) garde le menu du
navigateur. L'attribut `data-hack-menu-suppress` marque une zone de l'OS qui a
son propre menu (`HackMenuAttributes.Suppress`).

### 5. Ordre et focus

- Une action s'exécute **après** la fermeture du menu et le retour du focus à
  l'élément d'origine, pour qu'une boîte ou un renommage en place qu'elle ouvre
  garde le focus. Un appui hors du menu ne rend pas le focus (l'utilisateur l'a
  mis ailleurs) et continue vers sa cible, comme dans Windows.
- La surbrillance est annoncée par `aria-activedescendant` sur le panneau qui a
  le focus, plutôt que par un focus déplacé d'entrée en entrée.
- Ouvert au clavier, le menu met sa première entrée en surbrillance ; ouvert à
  la souris ou au doigt, aucune.

### 6. Erreurs

Un menu dont le constructeur, `Opening` ou la vérification échoue n'ouvre rien ;
une action qui lève est attrapée. Les deux sont écrits dans `/var/log/syslog`
par `ILogger<ContextMenuService>` ; le shell continue.

### 7. Syntaxe Razor

Une valeur d'attribut qui contient des guillemets s'écrit avec `@(…)` :
`@attributes="@(Menus.Attach("rows", row))"`, `OnClick="@(item => Export(item, "csv"))"`.
Les exemples de l'ADR 0150 et du plan sans `@(…)` ne compilent pas tels quels.

### 8. Icônes

`HackIcon` et `HackIcons` vivent dans `HackerOs.AppSdk.Icons` (à côté
d'`IconLibrary`) ; `HackerOs.AppSdk.Blazor` référence désormais ce paquet.

## Conséquences

- Le chantier 3 construit les menus du contrôle de fichiers sur ce modèle et
  retire le `MudMenu` de `FileView` ; le chantier 5 crée la portée du bureau
  (`ContextMenuService.CreateScope(null)`) et marque ses zones.
- Nouvelles API non livrées (`PublicAPI.Unshipped.txt`) dans `AppSdk.Icons`,
  `AppSdk.Blazor` et `Platform.Blazor`, publiées avec le SDK 2.0 (chantier 6).

## Références

- ADR 0063, 0078, 0148, 0150, 0153.
- [`context-menu.md`](../../framework/context-menu.md),
  [`embedded-components.md`](../../framework/embedded-components.md).
