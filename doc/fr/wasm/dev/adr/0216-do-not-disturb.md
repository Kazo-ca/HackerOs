> [English version](../../../../en/wasm/dev/adr/0216-do-not-disturb.md)

# ADR 0216 : mode « Ne pas déranger »

## Statut

Accepté le 2026-10-31, issue [#296](https://github.com/Kazo-ca/HackerOs/issues/296), fait
dans la série `todo/` (branche `todo/22-do-not-disturb`). Complète les bulles de
notification ([ADR 0197](0197-notification-toasts.md)), le point des notifications non lues
([ADR 0196](0196-unread-notifications-dot.md)) et les sons de l'OS
([ADR 0204](0204-os-event-sounds.md)). Les cinq questions de l'issue ont été posées à Benoit
le 2026-10-31 ; les choix ci-dessous sont les recommandations, prises en attendant sa
réponse. Mise en œuvre : [`notification-toasts.md`](../notification-toasts.md).

## Contexte

- Chaque nouvelle notification fait une bulle pendant 6 s (ADR 0197) et joue le son de
  sa gravité (ADR 0204). Pendant une présentation ou un travail concentré, on veut couper
  les deux sans perdre les notifications.
- Les réglages de l'OS sont de vrais fichiers `.config` en couches : `/etc/hackeros/`
  pour la machine, `~/.config/hackeros/` pour l'utilisateur (ADR 0044).
- Le panneau de l'horloge sert aussi de volet sur téléphone (`ClockPanel`).

## Décision

1. **Ce que fait le mode.** Tant que « Ne pas déranger » est activé, une nouvelle
   notification ne fait **ni bulle ni son**. Elle va dans le panneau de l'horloge comme
   d'habitude, et le point des non-lues (ADR 0196) s'allume toujours : rien n'est perdu.
   **Tout est coupé, erreurs comprises.** Les autres sons de l'OS (ouverture et fermeture
   de session, boîtes de message) ne changent pas. Activer le mode masque les bulles déjà
   affichées ; le désactiver ne rejoue pas ce qui est arrivé entre-temps.
2. **Où l'activer.** Aux deux endroits :
   - un interrupteur « Ne pas déranger » en haut de la section Notifications du panneau
     de l'horloge, donc aussi dans le volet du téléphone ;
   - une nouvelle page **Paramètres ▸ Notifications** (`--page=notifications`), avec le
     même interrupteur et l'origine du réglage (badge « Système » / « Utilisateur »).
3. **Où il est enregistré.** Par utilisateur, dans le document en couches
   `notifications` : clé `doNotDisturb` (booléen, `false` par défaut) de
   `~/.config/hackeros/notifications.config`. Un administrateur peut fixer la valeur par
   défaut de la machine dans `/etc/hackeros/notifications.config`. Le fichier est
   surveillé : une modification avec `nano` s'applique tout de suite. Le réglage suit
   l'utilisateur et survit au rechargement.
4. **Service.** `DoNotDisturbService` (Platform.Core, portée de la session) lit le
   document, expose `IsEnabled` et `Changed`, et écrit la couche utilisateur
   (`SetAsync`). Les bulles (`NotificationToasts`) le traitent comme le panneau ouvert
   (`NotificationToastFeed.IsSuppressed`). Le serveur de sons (`OsSoundService`) ne joue
   pas le son d'une notification publiée pendant que le mode est actif.
5. **Indicateur.** Une lune (icône Lucide `moon`) dans la zone de notification de la
   barre des tâches, à côté de l'horloge, avec l'infobulle et le nom accessible
   « Ne pas déranger est activé ». Un clic ouvre le panneau de l'horloge, où se trouve
   l'interrupteur. Sur téléphone, la même lune s'affiche sur la poignée du volet, dont le
   nom l'annonce aussi.

## Conséquences

- Les apps n'ont rien à changer, le SDK ne change pas.
- Nouveau document listé dans `OsSettingsDocumentRegistry` (`notifications`), nouveau
  service public `DoNotDisturbService` et nouveau composant `DoNotDisturbIndicator`
  (`PublicAPI.Unshipped.txt`).
- Pas de plage horaire automatique ni d'exceptions par app : à ajouter plus tard si le
  besoin vient (nouvelle issue `TODO`).
