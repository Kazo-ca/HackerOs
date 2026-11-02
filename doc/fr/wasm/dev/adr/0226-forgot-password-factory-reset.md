> [English version](../../../../en/wasm/dev/adr/0226-forgot-password-factory-reset.md)

# ADR 0226 : réinitialiser l'OS depuis l'écran de connexion (mot de passe oublié)

## Statut

Accepté le 2026-11-02 (demandé par Benoit, branche
`login/forgot-password-factory-reset`). Les trois questions ci-dessous ont été
posées à Benoit dans la conversation ; les choix recommandés sont appliqués
tant qu'il n'en a pas choisi d'autres.

- **Complète :** [ADR 0056](0056-sdk-apis-for-system-apps.md) §11
  (redémarrage et réinitialisation d'usine par Paramètres).
- **Voir aussi :** [ADR 0187](0187-guest-start-and-sign-in-credits.md)
  (écran de connexion, invité) et
  [ADR 0120](0120-passwd-and-administrator-password-reset.md)
  (un administrateur remet le mot de passe d'un autre compte).

## Contexte

Benoit (2026-11-02) : « Ajoute la possibilité de réinitialiser l'OS sur la
page de connexion si on a oublié le mot de passe. Ça efface toutes les
données. Ce comportement doit pouvoir être désactivé lors du déploiement de
l'application. »

Un administrateur peut déjà remettre le mot de passe d'un autre compte
(ADR 0120) et effacer la machine depuis Paramètres ▸ Système (ADR 0056 §11).
Mais quand le seul administrateur a oublié son mot de passe, rien ne permet de
revenir : il faut vider soi-même le stockage du site dans le navigateur. Sur
un vrai ordinateur, on réinstalle le système.

## Décision

1. **Lien « Mot de passe oublié ? »** sous le formulaire de connexion par mot
   de passe. Il n'apparaît pas pour l'invité seul (pas de mot de passe) ni sur
   la première configuration.
2. **Confirmation (question 3, choix A).** Le lien ouvre une boîte modale
   (`role="alertdialog"`) qui liste ce qui est perdu (comptes avec mot de
   passe et clés d'accès, fichiers, apps installées et leurs données,
   réglages, thèmes et journaux) et dit que c'est irréversible. Le bouton
   « Effacer et redémarrer » reste désactivé tant que le mot `EFFACER`
   (`ERASE` en anglais) n'est pas tapé ; la casse et les espaces autour ne
   comptent pas. Échap ou « Annuler » referme la boîte.
3. **Même effacement que Paramètres.** `SystemPowerService.FactoryResetFromSignInAsync`
   fait la séquence de `FactoryResetAsync` : vider les journaux, fermer ce qui
   reste de session, supprimer la base IndexedDB de l'OS, recharger la page.
   L'OS redémarre sur la première configuration. Aucun administrateur ne l'a
   autorisé : l'entrée d'audit `system.factory-reset` porte `admin = -`,
   `requestedBy = -` et `origin = sign-in`.
4. **Désactivable au déploiement (question 1, choix A).** La clé
   `HackerOs:SignIn:FactoryReset:Enabled` de `wwwroot/appsettings.json`
   (`SignInFactoryResetSettings`), lue une fois au démarrage comme
   `ResourceLimits`. On la change dans les fichiers publiés sans recompiler ;
   le serveur HackerOS sert les mêmes fichiers. À `false`, le lien disparaît
   et `SignInFactoryReset.RunAsync` n'efface rien même s'il est appelé.
   Le service worker (`service-worker.published.js`) met désormais
   `appsettings.json` en cache **sans** son empreinte d'intégrité : avant,
   un `appsettings.json` modifié après publication faisait échouer
   l'installation de tout le cache hors ligne (vérifié dans Chromium). Une
   modification faite après qu'un navigateur a installé la version n'y
   arrive qu'à la version suivante.
5. **Activé par défaut (question 2, choix A).** Une clé absente ou illisible
   vaut `true` ; le `appsettings.json` publié l'écrit explicitement.

## Questions posées à Benoit

1. Comment désactiver au déploiement ? **A (retenu)** clé dans
   `appsettings.json`, modifiable sans recompiler ; B propriété MSBuild
   compilée dans l'OS ; C les deux, MSBuild prioritaire.
2. Valeur par défaut ? **A (retenu)** activée ; B désactivée.
3. Confirmation ? **A (retenu)** liste des pertes et mot à taper ; B simple
   boîte « Annuler / Réinitialiser ».

## Options écartées

- **Propriété MSBuild seule** : il faudrait recompiler l'OS pour un choix de
  déploiement, alors que les autres réglages d'hôte sont dans
  `appsettings.json`.
- **Demander une preuve d'identité** (question secrète, courriel) : l'OS est
  local au navigateur, sans compte en ligne ; quiconque devant l'écran peut
  de toute façon vider le stockage du site.

## Conséquences

- Quiconque devant l'écran de connexion peut effacer la machine, comme on
  peut réinstaller un ordinateur. Un déploiement partagé (poste public,
  démonstration) met la clé à `false`.
- Comme la réinitialisation de Paramètres, seule la base de l'OS est
  supprimée. Les bases des sites web (`hackweb.*`) et les fichiers des apps
  installées dans le Cache Storage restent dans le navigateur sans être
  utilisés ; les clés d'accès restent dans l'authentificateur.
- Un autre onglet ouvert sur l'OS ferme sa connexion à la base
  (`onversionchange`) et ne fonctionne plus jusqu'à son rechargement.

## Mise en œuvre

- `Platform.Core` : `SystemPowerService.FactoryResetFromSignInAsync`.
- `Ecosystem.Host` : `SignInFactoryResetSettings` (clé de déploiement),
  `SignInFactoryReset` (`IsOffered`, `RunAsync`), composant
  `SignInFactoryResetPrompt` (`.razor`, `.razor.cs`, `.razor.css`), branché
  dans `App.razor` ; textes `FactoryReset.*` dans `Strings.resx` et
  `Strings.fr.resx`.
- `Ecosystem` : `service-worker.published.js` (`offlineAssetsWithoutIntegrity`).
- Documentation : [`sign-in-factory-reset.md`](../sign-in-factory-reset.md).
