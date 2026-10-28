> [English version](../../../en/wasm/dev/screens-settings.md)

# Paramètres ▸ Écrans

Chantier `multi-monitor/7-screen-settings` du [plan multi-moniteur](plans/multi-monitor-plan.md),
mise en œuvre de l'[ADR 0179 §7](adr/0179-per-screen-taskbars-and-other-screen-menus.md). La page
lit et écrit `screens.config` ; elle s'appuie sur le service d'écrans
([`screen-service.md`](screen-service.md)) et sur les barres des tâches par écran
([`per-screen-taskbars.md`](per-screen-taskbars.md)), qui suivent le fichier.

## 1. Ce que montre la page

| Bloc | Contenu | Écrit |
| --- | --- | --- |
| Barre des tâches de l'écran 1 | Choix `all` (toutes les fenêtres de tous les écrans) ou `own` (celles de l'écran 1), avec l'origine de la valeur (défaut, système, utilisateur) et le verrou de l'administrateur. | `[taskbar] primary-taskbar` de la couche utilisateur |
| Écrans retenus | L'écran 1 (cet onglet du navigateur), puis chaque écran 2 à 9 listé dans `open` ou ayant une place valide : état « ouvert, ou proposé à la prochaine connexion » ou « fermé », place `largeur × hauteur à (gauche, haut)`. Bouton **Oublier** sur un écran fermé qui a une place. | Retire la ligne numérotée de la couche utilisateur |
| Ouvrir et déplacer les écrans | Où sont les commandes : Autre écran ▸ Nouvel écran (clic droit sur l'icône ou sur Agrandir), raccourcis `Super+Shift+←/→`, carte **Restaurer les écrans**, bouton `⤢`, permission de gestion des fenêtres (Chrome, Edge). | — |

Le fichier est `~/.config/hackeros/screens.config` ; `/etc/hackeros/screens.config` donne les
valeurs par défaut du système (un administrateur l'écrit, par exemple avec `sudo nano`).

## 2. Pourquoi la page n'a pas de boutons d'écran

Paramètres est une app de base : elle passe seulement par le SDK public, et la série ne touche pas au
SDK des apps (question Q1 du plan). La page n'appelle donc pas `ScreenService` : elle ouvre le
document `screens` par `HackSettings.OpenSystemAsync(AppContext, "screens")`, que l'OS liste
maintenant dans `OsSettingsDocumentRegistry`. Les commandes Nouvel écran, Restaurer, Identifier (le
voile au survol du sous-menu) et Plein écran restent là où elles sont déjà.

## 3. Qui écrit quoi

| Ligne | Écrite par | Suivie par |
| --- | --- | --- |
| `[taskbar] primary-taskbar` | Paramètres ▸ Écrans, `ScreenService.SetPrimaryTaskbarAsync` | `ScreenService` (surveillance du document) : les barres changent tout de suite |
| `[screens] open` | `ScreenService` seulement | Paramètres ▸ Écrans (affichage) |
| `[screens] 2` à `9` | `ScreenService` (place), Paramètres ▸ Écrans (Oublier) | `ScreenService` à la prochaine ouverture de l'écran |

La page ne change pas `open` : le service garde sa liste en mémoire pendant la session et la réécrit
à chaque ouverture ou fermeture d'un écran. C'est pourquoi seul un écran fermé peut être oublié. Un
écran oublié rouvre à la place par défaut (1280 × 800, ou un moniteur libre avec la permission).

La page suit le fichier (`HackSettingsDocument.Changed`) : un écran ouvert ou déplacé, ou une
modification au terminal, s'affiche sans recharger la page.

## 4. Fichiers

| Fichier | Rôle |
| --- | --- |
| `Apps/System/HackerOs.Apps.Settings/ScreensPanel.razor(.cs, .css)` | La page, `RememberedScreen`, `RememberedPlace`. |
| `Apps/System/HackerOs.Apps.Settings/SettingsWindow.Navigation.cs` | Entrée `screens` dans le groupe Personnalisation (`--page=screens`). |
| `Platform/HackerOs.Platform.Core/Settings/OsSettingsDocumentRegistry.cs` | Document `screens` ouvert aux apps. |
| `Tests/HackerOs.Apps.Settings.Tests/ScreensPanelTests.cs` | Clés identiques au schéma de l'OS, choix `own` écrit, liste et places, Oublier, changement extérieur, français. |
| `Tests/HackerOs.Tests.Support/MinimalAppExecutionContext.cs` | Paramètre `systemSettings` pour les tests de panneaux. |

## 5. Limites

- Pas de nom de moniteur : le fichier ne le garde pas, et le SDK n'expose pas le service d'écrans.
- Pas d'état de la permission `window-management` sur la page, pour la même raison ; la page explique
  quand le navigateur la demande.
- Une place oubliée pendant qu'un écran est ouvert n'est pas possible : il faut d'abord le fermer.
- Sur mobile, la page reste visible mais aucun écran secondaire ne s'ouvre (`MobileShell`).
