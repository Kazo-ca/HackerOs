> [English version](../../../en/wasm/dev/notification-toasts.md)

# Bulles de notification

Chantier 1 de la série barre des tâches (issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242)),
mise en œuvre de l'[ADR 0197](adr/0197-notification-toasts.md), fait dans la série `todo/`.

## 1. Ce que voit l'utilisateur

- Chaque nouvelle notification de l'OS ou d'une app apparaît en **bulle** : en bas à
  droite, au-dessus de la barre de l'écran 1 sur ordinateur, en haut de l'écran sur
  téléphone. Elle porte le nom de l'app (ou « HackerOS »), le titre, le texte, les
  boutons d'action et un « × ».
- Une bulle part après **6 s**. La souris ou le focus clavier sur la bulle la retient ;
  elle repart pour 6 s quand ils la quittent. 3 bulles au plus : la plus ancienne laisse
  sa place.
- Le « × » masque la bulle. La notification reste dans le panneau de l'horloge (le volet
  sur téléphone). La fermer dans le panneau retire aussi sa bulle.
- Pas de bulle pendant que le panneau est ouvert : la notification arrive dans la liste.
- **Mise à jour prête** : une fois connecté, « HackerOS x.y.z est disponible. Les apps
  ouvertes seront fermées. » arrive en bulle et reste dans le panneau avec le bouton
  « Redémarrer pour mettre à jour ». Sur l'écran de connexion, l'encadré du coin en bas
  reste, avec le même bouton.

## 2. Architecture

| Élément | Rôle |
| --- | --- |
| `NotificationToastFeed` (interne, `Platform.Blazor/Shell`) | Échantillonne `INotificationQueue` une fois par seconde simulée, garde les identifiants déjà vus, décide des bulles (`Toasts`), de leur fin (`HideAtUtc`), du maintien (`Hold`/`Release`), de `Close` et de `IsSuppressed` (panneau ouvert). Ne montre pas une notification qui a déjà plus de 6 s quand il la voit pour la première fois. |
| `NotificationToasts.razor` (+ `.razor.cs`, `.razor.css`) | Dessine la pile dans une région `aria-live="polite"`. Paramètres : `Placement` (`AboveTaskbar` ou `TopOfScreen`) et `IsPanelOpen`. |
| `NotificationActionRunner` (interne) | Exécute une action pour le panneau et pour les bulles : d'abord `ShellNotificationActions`, puis `NotificationAction.LaunchApp`. Ferme la notification quand l'action a été exécutée. |
| `ShellNotificationActions` (public, portée de session) | Registre des actions que l'OS exécute lui-même : `Register(actionId, handler)` rend un jeton qui retire l'inscription. Inscrit dans `EcosystemServiceCollectionExtensions`. |
| `UpdateReadyNotification` (public) | Publie « Update ready » (source `org.hackeros.update`, sans expiration, action `hackeros.update.restart`) une fois par utilisateur et par texte, et inscrit « Redémarrer ». |
| `App.razor` (Ecosystem.Host) | Crée `UpdateReadyNotification` avec `RestartForUpdateAsync`. Après chaque rendu du bureau, appelle `Show` pour l'utilisateur connecté, dans sa langue. N'affiche l'encadré `update-notice` que hors session. |
| `DesktopShell.razor` / `MobileShell.razor` | Posent `<NotificationToasts>` : au-dessus de la barre de l'écran 1 avec `IsPanelOpen = _clockPanel.IsOpen`, ou en haut avec `IsPanelOpen = _isShadeOpen`. |

Le contrat `INotificationQueue` ne change pas. Le SDK des apps ne change pas : toute
notification publiée par `notifications.post` fait une bulle.

## 3. Styles

`NotificationToasts.razor.css` reprend les jetons du panneau (`--hos-bg-surface`,
`--hos-border`, `--hos-accent`, `--hos-warning`, `--hos-error`, `--hos-success`,
`--hos-font-mono`). La pile est en `z-index: 5000`, au-dessus des fenêtres et de la barre,
sous les menus de fenêtre et les voiles d'écran (9000). Elle laisse passer les clics ;
seules les bulles les prennent. L'entrée (glissé + fondu, 180 ms) ne joue que sous
`[data-motion="enabled"]` et `prefers-reduced-motion: no-preference`.

## 4. Tests

- `NotificationToastFeedTests` : bulle à la seconde suivante, départ après 6 s,
  pile de 3, maintien et reprise, « × », fermeture dans le panneau, panneau ouvert,
  anciennes notifications au démarrage, utilisateur de la session et déconnexion.
- `UpdateReadyNotificationTests` : une fois par utilisateur, sans expiration,
  « Redémarrer » exécuté puis notification fermée, nouvelle version qui remplace,
  action d'app laissée à l'app, désinscription.

## 5. Liste des tâches

- [x] `NotificationToastFeed` et ses tests.
- [x] `NotificationToasts` sur le bureau (écran 1) et sur téléphone.
- [x] Actions communes au panneau et aux bulles (`NotificationActionRunner`, `ShellNotificationActions`).
- [x] « Update ready » en notification persistante avec « Redémarrer » ; encadré gardé sur l'écran de connexion.
- [x] ADR 0197, docs, `features.md`, mémoire, entrée de changement.
- [ ] « Ne pas déranger » : à décider (question Q5 posée à Benoit).
