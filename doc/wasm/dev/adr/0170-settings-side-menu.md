# ADR 0170 : menu latéral réductible dans Paramètres

## Statut

Accepté le 2026-10-25 à la demande de Benoit (« la page des settings, j'aime pas
que ce soit des tabs dans le haut pour naviguer entre les pages. J'aimerais avoir
un menu à gauche, que on peut réduire ou développer »). Remplace la navigation par
onglets `MudTabs` de l'app Paramètres. Ne change pas le contenu des pages.

## Contexte

Paramètres compte 15 à 17 pages (Sauvegarde selon l'hôte, Application pour
`--app=<id>`). Les onglets `MudTabs` du haut débordaient en largeur et
demandaient de défiler ; les pages étaient repérées par leur index
(`AppearanceTabIndex`, `FixedTabCount`, « Application » gardé en dernier pour ne
pas décaler les autres index).

## Décision

1. **Menu à gauche.** `SettingsWindow` affiche un `<nav>` à gauche et la page
   active à droite. Le menu est une liste d'onglets verticale (WAI-ARIA :
   `role="tablist"`, `aria-orientation="vertical"`, `role="tab"`, une
   `role="tabpanel"`) : les flèches haut/bas, Début et Fin changent de page, les
   tests E2E qui cherchent `GetByRole(AriaRole.Tab, "…")` restent valides.
2. **Groupes.** Les pages sont groupées : Personnalisation (Apparence, Bureau,
   Accessibilité, Langue), Applications (Associations de fichiers, Capacités
   accordées, Permissions, Applications installées, Composants de l'OS, Menus des
   fichiers, Application), Réseau (Connexion au serveur, Synchronisation),
   Système (Utilisateurs, Mémoire, Sauvegarde, À propos du système).
3. **Réduire et développer.** Un bouton en tête du menu (« Réduire le menu » /
   « Développer le menu », `aria-expanded`) passe d'un menu de 14 rem avec
   libellés à une barre d'icônes de 3,25 rem ; les titres de groupe deviennent
   des séparateurs, chaque icône garde son nom en infobulle et en nom
   accessible. L'état vit dans la fenêtre : il n'est pas enregistré.
4. **Fenêtre étroite et mobile.** Sous 36 rem de large (requête de conteneur
   sur le corps de la fenêtre, donc aussi une petite fenêtre du bureau), le menu
   est toujours une barre d'icônes ; son bouton l'ouvre par-dessus la page avec
   un voile, et choisir une page le referme.
5. **Pages par identifiant.** Chaque page a un identifiant stable
   (`appearance`, `desktop`, `accessibility`, `language`, `file-associations`,
   `capabilities`, `permissions`, `installed-apps`, `os-components`,
   `file-menus`, `server`, `sync`, `users`, `memory`, `backup`, `about`,
   `application`). `--page=<id>` accepte maintenant toutes les pages sauf
   `application`, réservée à `--app=<id>` (ADR 0096 §4) ; un identifiant
   inconnu est ignoré. `--page=appearance` et `--page=desktop` de l'ADR 0160 §8
   gardent leur sens.
6. La règle globale `.hos-tab-header` de `css/app.css`, propre aux onglets de
   Paramètres, est retirée.

## Conséquences

- Une nouvelle page s'ajoute dans `SettingsWindow.AllPages`
  (`SettingsWindow.Navigation.cs`, identifiant, groupe, libellé, icône) et dans
  le `@switch` de `SettingsWindow.razor`, sans index à recalculer.
- Les textes `Nav.*` existent en anglais et en français ; un test vérifie que
  chaque libellé et groupe du menu est traduit.
- Mémoriser l'état réduit d'une ouverture à l'autre reste possible plus tard
  (par exemple dans `~/.config/hackeros/`), sans changer cette décision.
