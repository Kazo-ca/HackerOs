> [English version](../../../../en/wasm/dev/adr/0197-notification-toasts.md)

# ADR 0197 : bulles de notification et « Update ready » en notification

## Statut

Accepté le 2026-10-30, chantier 1 de la série barre des tâches (issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242), décisions 1 et 2 prises avec
Benoit le 2026-10-29), fait dans la série `todo/` (branche `todo/11-notification-toasts`).
S'appuie sur la file de notifications (`INotificationQueue`), le panneau de l'horloge
(`ClockPanel`), les barres par écran
([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md), la zone de notification
reste sur l'écran 1) et le redémarrage après un nouveau build
([ADR 0162](0162-restart-after-a-host-build-change.md),
[ADR 0174](0174-announce-a-new-version-to-open-sessions.md)). Tient compte du point des
notifications non lues (ADR 0196, [PR #294](https://github.com/Kazo-ca/HackerOs/pull/294)),
sans en dépendre. Mise en œuvre : [`notification-toasts.md`](../notification-toasts.md).

## Contexte

- Les notifications de l'OS et des apps (`notifications.post`) n'apparaissent que dans
  le panneau de l'horloge (sur téléphone, le volet). Rien ne prévient l'utilisateur
  qu'une notification arrive.
- `INotificationQueue` ne signale aucun changement : ses lecteurs l'interrogent
  (l'ADR 0196 l'échantillonne une fois par seconde simulée).
- « Update ready » est un `aside` fixe de l'hôte (`App.razor`), placé au-dessus de la
  barre par la PR #258. Il n'est pas dans le panneau, on ne peut pas le fermer, et il
  couvre le coin du bureau jusqu'au redémarrage.
- Dans le panneau, une action de notification ne fait quelque chose que si elle lance
  une app (`NotificationAction.LaunchApp`). Rien ne permet à l'OS de répondre à une
  action comme « Redémarrer ».

## Décision

1. **Une bulle pour chaque nouvelle notification.** Le composant `NotificationToasts`
   (Platform.Blazor) montre chaque nouvelle notification de l'utilisateur de la
   session, de l'OS comme des apps :
   - sur ordinateur, en bas à droite, au-dessus de la barre de l'**écran 1** (la zone de
     notification y reste, ADR 0179) ;
   - sur téléphone, **en haut de l'écran**, comme les notifications des téléphones (la
     barre de navigation et le clavier occupent le bas) ;
   - **3 bulles au plus**. Une nouvelle bulle pousse la plus ancienne dehors.
2. **Durée.** Une bulle reste **6 s** (temps simulé), puis disparaît. Elle reste en
   place tant que la souris ou le focus clavier est dessus, puis repart pour 6 s
   (WCAG 2.2.1, laisser le temps de lire). Son « × » masque la bulle seulement : la
   notification reste dans le panneau, comme sous Windows. Une notification fermée dans
   le panneau emporte sa bulle.
3. **Ce qui compte comme nouveau.** La file est échantillonnée une fois par seconde
   simulée, comme pour l'ADR 0196. Le contrat `INotificationQueue` ne change pas. Une
   notification est nouvelle la première fois qu'un échantillon la voit. Si elle a déjà
   plus de 6 s à ce moment, elle ne fait pas de bulle : une connexion ne rejoue pas le
   panneau en bulles. **Panneau ouvert = pas de bulle** : ce qui arrive va directement
   dans la liste ouverte, et ne fait pas de bulle après la fermeture.
4. **Actions communes au panneau et aux bulles.** `NotificationActionRunner` exécute une
   action de la même façon aux deux endroits. Une action inscrite par l'hôte dans
   `ShellNotificationActions` (service public, portée de la session) exécute son
   gestionnaire. Une action `LaunchApp` lance son app. Dans les deux cas, la notification
   est ensuite fermée. Toute autre action appartient à son app, comme avant.
5. **« Update ready » devient une notification.** Une fois un utilisateur connecté,
   l'hôte publie, par `UpdateReadyNotification`, une notification qui n'expire pas :
   source `org.hackeros.update`, titre « HackerOS x.y.z est disponible. » (ou « Une mise
   à jour de HackerOS est prête. »), texte « Les apps ouvertes seront fermées. », action
   « Redémarrer pour mettre à jour » (`hackeros.update.restart`). Elle fait une bulle à
   son arrivée et reste dans le panneau jusqu'à ce qu'on la ferme ou qu'on redémarre.
   Elle est publiée une fois par utilisateur et par texte. Si on la ferme, elle ne
   revient pas. Si la version devient connue, elle remplace la précédente.
   **Écran de connexion** : il n'y a ni session, ni file, ni panneau. L'encadré de
   l'hôte reste donc là, dans le coin en bas, avec son bouton. Il n'apparaît plus une
   fois connecté.
6. **Nom de la source.** Une bulle affiche le nom de l'app dans la langue de la session
   quand le catalogue la connaît, et « HackerOS » pour les sources de l'OS
   (`org.hackeros.*`, qui ne sont pas des apps), sinon l'identifiant.
7. **Apparence.** Mêmes jetons que le panneau (bordure gauche de la couleur de la
   gravité), entrée en glissé et fondu de 180 ms, coupée par « Réduire les animations »
   (ADR 0105). Le conteneur est une région `aria-live="polite"` présente avant la
   première bulle, pour que les lecteurs d'écran annoncent chacune.

## Conséquences

- Les apps n'ont rien à changer : toute notification déjà publiée fait une bulle. Le SDK
  ne change pas.
- Nouvelle API publique de Platform.Blazor (`PublicAPI.Unshipped.txt`) :
  `NotificationToasts`, `NotificationToastPlacement`, `ShellNotificationActions`,
  `UpdateReadyNotification`. Un hôte qui n'inscrit pas `ShellNotificationActions` n'offre
  pas « Redémarrer » dans la notification.
- Les classes `above-taskbar` et `above-mobile-nav` de l'encadré de l'hôte (PR #258)
  sont retirées : il ne s'affiche plus que sur les écrans de connexion.
- « Ne pas déranger » n'est pas fait : à décider plus tard (question posée à Benoit).
- Le point des notifications non lues (ADR 0196) et les bulles lisent la même file sans
  se gêner : une bulle ne marque pas la notification comme lue.

## Questions posées à Benoit (2026-10-30)

Q1 bulles en haut sur téléphone, Q2 survol qui arrête le compte à rebours, Q3 pas de
bulle panneau ouvert, Q4 encadré gardé sur l'écran de connexion, Q5 « Ne pas déranger »
hors chantier. La recommandation est appliquée partout en attendant sa réponse.
