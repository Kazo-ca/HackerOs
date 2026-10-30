> [English version](../../../../en/wasm/dev/adr/0196-unread-notifications-dot.md)

# ADR 0196 : point des notifications non lues à côté de l'horloge

## Statut

Accepté le 2026-10-30. Demande de Benoit du 2026-10-30 (fil « Expliquer le point
jaune sur Applications »). Les questions Q1 à Q6 ci-dessous lui ont été posées
dans le fil ; la recommandation est appliquée tant qu'il n'a pas choisi autre
chose.

- **Remplace :** la pastille du menu Démarrer et du bouton Accueil de
  l'[ADR 0186](0186-whats-new-change-list.md) §4.5. Le reste de l'ADR 0186 ne
  change pas (notification après la connexion, page Paramètres ▸ Nouveautés).
- **Complète :** [ADR 0134](0134-automatic-server-connection-and-status-icon.md)
  (zone de notification de la barre des tâches).

## Contexte

- Depuis l'ADR 0186, un point jaune s'affichait en haut à droite du bouton
  « Applications » (et du bouton Accueil sur téléphone) tant qu'il restait des
  nouveautés non vues. Rien n'expliquait ce point : pas d'infobulle, et il
  n'avait pas de lien visible avec la notification « 3 nouveautés depuis votre
  dernière visite » rangée dans le panneau de l'horloge.
- Benoit préfère que ce soit la zone de notification qui le signale : un point
  à côté de l'heure, et un clic sur la notification qui ouvre la bonne page de
  Paramètres (le bouton **Voir** le faisait déjà).
- La file des notifications (`INotificationQueue`) ne signale aucun changement
  et ne sait pas ce qui a été lu.

## Questions posées à Benoit

| # | Question | Choix (⭐ recommandé) |
|---|----------|------------------------|
| Q1 | Qu'est-ce qui allume le point de l'horloge ? | **A ⭐ toute notification non lue** \| B seulement les nouveautés |
| Q2 | Quand s'éteint-il ? | **A ⭐ à l'ouverture du panneau** (les notifications restent dans la liste) \| B quand la liste est vide |
| Q3 | Le point du bouton Applications ? | **A ⭐ retiré** (sur téléphone, il passe sur la poignée du volet) \| B gardé en plus |
| Q4 | Un point sur l'app Paramètres du menu Démarrer ? | **A ⭐ non** \| B oui |
| Q5 | L'infobulle ? | **A ⭐ sur l'horloge**, avec le nombre, aussi nom accessible \| B sur le point seulement |
| Q6 | Nouveautés vues depuis Paramètres sans passer par la notification ? | **A ⭐ la notification se ferme seule** \| B elle reste |

## Décision

### 1. Le point de l'horloge

- Le bouton de l'horloge de la barre des tâches porte un point (couleur
  `--hos-taskbar-attention`, la même que l'ancienne pastille) tant qu'au moins une
  notification active de l'utilisateur n'a pas été lue. Toutes les sources
  comptent : nouveautés, stockage faible, OOM, apps.
- Son nom accessible et son infobulle (`title`) donnent le nombre :
  « Horloge système, notifications et calendrier : 3 notifications non lues ».
  Sans notification non lue, l'infobulle reste « Horloge système, notifications
  et calendrier ».
- Contrat du paquet `HackerOs.Taskbar.Blazor` :
  `ITaskbarClockPanelSource.UnreadCount` (0 par défaut, les autres hôtes ne
  changent pas). `TaskbarClockPanelSourceAdapter.SetUnreadCount` le règle.

### 2. Lu et non lu

- Ouvrir le panneau de l'horloge marque comme lues toutes les notifications
  qu'il montre, y compris celles qui arrivent pendant qu'il est ouvert. Elles
  restent dans la liste jusqu'à ce que l'utilisateur les ferme (×), comme le
  centre de notifications de Windows.
- `NotificationAttention` (Platform.Blazor, `Shell/`) tient l'état lu en mémoire
  pour la page : une notification ne vit de toute façon que le temps de la page.
  La file ne signalant aucun changement, il l'échantillonne une fois par seconde
  simulée, au rythme de l'horloge, et ne prévient que quand le nombre change.
- Le contrat `INotificationQueue` (Simulation.Abstractions) ne change pas.

### 3. Téléphone

- Le point passe du bouton Accueil à la poignée du volet de notifications, en
  haut de l'écran, à droite du trait. Même règle : ouvrir le volet lit les
  notifications. Le nom accessible et l'infobulle de la poignée donnent le nombre.

### 4. Nouveautés

- Le bouton Applications et le bouton Accueil n'ont plus de pastille.
  `ITaskbarLauncher.HasAttention` et `MobileSystemNavigationBar.HomeHasAttention`
  restent dans leurs paquets pour d'autres hôtes ; HackerOS ne s'en sert plus.
- La notification après la connexion ne change pas : son bouton **Voir** ouvre
  Paramètres ▸ Nouveautés et la ferme.
- Quand toutes les nouveautés sont vues, où que ce soit (page ouverte depuis
  Paramètres ou depuis la notification), `WhatsNewService` ferme sa
  notification : elle n'a plus rien à dire.
- Pas de point sur l'app Paramètres.

## Conséquences

- Un seul endroit signale ce qui attend l'utilisateur, et il s'explique au
  survol.
- Toute notification allume désormais le point, pas seulement les nouveautés.
- L'état lu est perdu au rechargement de la page, comme les notifications
  elles-mêmes.
- Tests : `NotificationAttentionTests`, `TaskbarLocalizationTests`
  (point et nom de l'horloge), `TaskbarClockPanelSourceAdapterTests`,
  `WhatsNewServiceTests` (notification fermée quand tout est vu).
