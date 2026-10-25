# App Paramètres : navigation

App système `org.hackeros.settings` (`Apps/System/HackerOs.Apps.Settings`). Ce
document décrit son cadre ; le contenu de chaque page a sa propre doc (voir
l'index).

## Menu latéral (ADR 0170)

- Le menu à gauche liste les pages par groupe : Personnalisation, Applications,
  Réseau, Système. La page Sauvegarde n'apparaît que si l'hôte la prend en
  charge (ADR 0099), la page Application que pour `--app=<id>` (ADR 0096 §4).
- Le bouton en tête réduit le menu à une barre d'icônes ou le développe avec ses
  libellés. L'état n'est pas enregistré.
- Dans une fenêtre de moins de 36 rem (téléphone, petite fenêtre), le menu reste
  une barre d'icônes ; son bouton l'ouvre par-dessus la page, choisir une page le
  referme.
- Clavier : Tab entre dans le menu sur la page active, flèches haut/bas, Début et
  Fin changent de page.

## Ouvrir une page

`--page=<id>` ouvre une page :

| Groupe | Identifiants |
|---|---|
| Personnalisation | `appearance`, `desktop`, `accessibility`, `language` |
| Applications | `file-associations`, `capabilities`, `permissions`, `installed-apps`, `os-components`, `file-menus` |
| Réseau | `server`, `sync` |
| Système | `users`, `memory`, `backup`, `about` |

`application` ne s'ouvre que par `--app=<id>`. Un identifiant inconnu est ignoré,
le dernier `--page=` connu gagne.

## Code

- `SettingsWindow.Navigation.cs` : `SettingsPage`, `AllPages`, `PagesFor`,
  `ReadPageArgument`, état réduit/ouvert, clavier.
- `SettingsWindow.razor` : `<nav>` puis `@switch (_activePage)`.
- `SettingsWindow.razor.css` : menu, état réduit, requête de conteneur
  `settings-body` pour la fenêtre étroite.
- Tests : `Tests/HackerOs.Apps.Settings.Tests/SettingsWindowTests.cs`
  (`SettingsAppArgumentTests`, `SettingsNavigationTests`).

Ajouter une page : une entrée dans `AllPages`, un `case` dans le `@switch`, un
libellé `Tab.*` en anglais et en français.
