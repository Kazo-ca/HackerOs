# App Paramètres (`org.hackeros.settings`)

## Objet

Décrit l'app système `Apps/System/HackerOs.Apps.Settings` : le panneau de
configuration de l'OS. Elle ne possède aucune donnée : chaque page lit et
écrit des fichiers de l'OS (paramètres en fichiers, ADR 0044) ou appelle une
façade `Hack*` du SDK. Les décisions qui l'ont façonnée sont l'ADR 0047
(manifeste), l'ADR 0048 (app système), l'ADR 0096 (arguments `--app=`), les
ADR 0105, 0120, 0121, 0160, 0170 (menu latéral) et les sections 8 à 11 de l'ADR 0056.

Fichiers clés : `SettingsApp.cs` (classe `[AppManifest]`, identifiant, capacités,
poids de ressources), `SettingsWindow.razor` et `SettingsWindow.razor.cs` (la
fenêtre : menu latéral puis `@switch` sur la page active),
`SettingsWindow.Navigation.cs` (liste des pages, état réduit, clavier),
`SettingsWindow.Mobile.cs` (préférences mobiles), un panneau `*Panel.razor` par page, `Strings.cs` et `Resources/` (textes
anglais et français). Le `manifest.json` est généré au build à partir de
`SettingsApp.cs` (ne pas l'éditer). Tests : `Tests/HackerOs.Apps.Settings.Tests`.

## Menu latéral (ADR 0170)

Les pages sont dans un menu à gauche, groupé : Personnalisation (Apparence,
Bureau, Accessibilité, Langue), Applications (Associations de fichiers à
Menus des fichiers, Application), Réseau (Connexion au serveur,
Synchronisation), Système (Utilisateurs, Mémoire, Sauvegarde, À propos).

- Le bouton en tête du menu le réduit à une barre d'icônes ou le développe avec
  ses libellés. L'état n'est pas enregistré (choix de Benoit, 2026-10-25) : le
  menu s'ouvre toujours développé.
- Dans une fenêtre de moins de 36 rem (téléphone, petite fenêtre ; requête de
  conteneur `settings-body`), le menu reste une barre d'icônes ; son bouton
  l'ouvre par-dessus la page, choisir une page le referme.
- Accessibilité : liste d'onglets verticale WAI-ARIA (`role="tab"`), flèches
  haut/bas, Début et Fin changent de page.
- Ajouter une page : une entrée dans `SettingsWindow.AllPages` (identifiant,
  groupe, libellé, icône), un `case` dans le `@switch` de `SettingsWindow.razor`,
  un libellé `Tab.*` en anglais et en français. Aucun index à recalculer.

## Pages

| Page (identifiant) | Panneau | Contenu principal |
|---|---|---|
| Apparence (`appearance`) | (dans `SettingsWindow.razor`) | Couche d'écriture (utilisateur ou système), thème du bureau et du mobile, accent, animations, thèmes utilisateur (`/etc/hackeros/themes` et `~/.config/hackeros/themes`, `UserThemeLibrary`) ; écrit `appearance.config`. |
| Bureau (`desktop`) | `DesktopPanel` | Options et disposition du bureau (`desktop.config`, ADR 0160). |
| Accessibilité (`accessibility`) | `AccessibilityPanel` | Contraste élevé, taille du texte, mouvement réduit (ADR 0105). |
| Langue (`language`) | `LanguagePanel` | Langue de l'interface. |
| Associations de fichiers (`file-associations`) | `FileAssociationsPanel` | Applications par défaut, couche système ou utilisateur (`HackFileAssociationLayer`, ADR 0121). |
| Autorisations accordées (`capabilities`) | (dans la fenêtre) | Capacités accordées à l'utilisateur. |
| Permissions (`permissions`) | `AppPermissionsPanel` | Décisions par app et approbations des capacités dangereuses (ADR 0049). |
| Applications installées (`installed-apps`) | `InstalledAppsPanel` | Liste des apps et de leurs commandes, désinstallation et réinstallation (ADR 0048). |
| Composants de l'OS (`os-components`) | `OsComponentsPanel` | Fournisseur choisi pour chaque contrat (`HackComponentProviders.GetOverviewAsync`, `SetProviderAsync`). |
| Menus des fichiers (`file-menus`) | `FileMenuEntriesPanel` | Entrées de menu inscrites par les apps (ADR 0151). |
| Connexion au serveur (`server`) | `ServerRelayPolicyPanel` (+ la fenêtre) | Serveur optionnel, relais réseau (ADR 0102, 0134). Visible dans tous les cas, l'état vient de `HackServerConnection`. |
| Synchronisation (`sync`) | (dans la fenêtre) | État de la synchro et conflits (ADR 0101). |
| Utilisateurs (`users`) | `UsersPanel` (+ `PasskeysPanel`) | Comptes, mots de passe, ajout et suppression de comptes par un administrateur, clés d'accès de la session (ADR 0120, 0168, 0169 ; voir [Page Utilisateurs](#page-utilisateurs)). |
| Mémoire (`memory`) | `MemoryPanel` | Fichier `/etc/hackeros/memory.config`, section `[oom-killer]` (ADR 0126). |
| Sauvegarde (`backup`) | `BackupPanel` | Présent seulement si `HackSystemBackup.IsSupportedFor` le permet (ADR 0099). |
| À propos (`about`) | (dans la fenêtre) | Autorité, redémarrage (`HackSystem.RebootAsync`), zone de danger : réinitialisation d'usine (`HackSystem.FactoryResetAsync`), confirmée par la saisie de `RESET`. |
| Application (`application`) | `AppDetailsPanel` | Ajouté seulement avec `--app=<id>` (ADR 0096) : détails et réglages d'une app. |

## Arguments de lancement

- `--page=<id>` : ouvre la page de cet identifiant (tableau ci-dessus), sauf
  `application` ; le « Changer le fond » et « Réglages du bureau » du bureau
  utilisent `appearance` et `desktop` (ADR 0160 §8). Une valeur inconnue est
  ignorée, le dernier `--page=` connu gagne.
- `--app=<id>` : ouvre la page « Application » de cette app ; un identifiant
  vide est ignoré, le dernier gagne.

## Accès aux données

| Donnée | Accès | Capacité |
|---|---|---|
| `appearance.config`, `mobile` (documents système) | `HackSettings.OpenSystemAsync` | `filesystem.system.*` pour écrire `/etc` |
| Apps installées, gestionnaires par défaut | `HackApps` (`GetInstalledAsync`, `GetDefaultHandlersAsync`, `SetDefaultHandlerAsync`, `ResetDefaultHandlerAsync`), `HackAppInstallInfo` | `apps.launch`, `apps.manage`, `file-associations.read` / `.write` |
| Serveur | `HackServerConnection` | `server.connection.manage` |
| Processus et services | `HackServiceController`, liste des processus | `process.list`, `services.manage` |
| Sauvegarde | `HackSystemBackup` | `system.backup` |
| Redémarrage, réinitialisation | `HackSystem` | `system.power` |
| Permissions, utilisateurs | `HackPermissionAdministration`, `HackUserAccounts` | `permissions.manage`, `users.manage` |

Les deux opérations d'administration (redémarrage, réinitialisation) font
confirmer un administrateur par l'OS dans sa propre fenêtre ; l'app ne voit
jamais le mot de passe ([`admin-authentication-agent.md`](admin-authentication-agent.md)).

## Page Utilisateurs

`UsersPanel` (ADR 0120, 0169) et `PasskeysPanel` (ADR 0168), de haut en bas :

- **Liste des comptes** : nom de connexion, nom affiché, badges
  « Administrateur », « Vous », nombre de clés d'accès, état `P`/`NP`/`L`. Pour
  un administrateur, un lien « Supprimer » sur chaque compte autre que le sien
  ouvre une confirmation avec la case « Supprimer aussi son dossier
  personnel `/home/<login>` », décochée par défaut
  (`HackUserAccounts.DeleteAsync(login, removeHome)`).
- **Ajouter un compte** (administrateur seulement) : nom de connexion, nom
  affiché, type standard ou administrateur, mot de passe initial et
  confirmation, vérifiés par `HackUserAccounts.ValidateNewAccount` avant
  l'envoi (`HackUserAccounts.CreateAsync`).
- **Mes clés d'accès** (tout le monde) : nom, date d'ajout et de dernière
  utilisation, badge « trousseau » si la clé ouvre le trousseau (PRF), bouton
  « Retirer », et un champ « Nom » + « Ajouter une clé d'accès » qui lance la
  demande du navigateur. Sans WebAuthn (page non sécurisée), une note remplace
  le formulaire. Voir [`passkeys.md`](passkeys.md).
- **Mon mot de passe**, puis pour un administrateur la **réinitialisation**
  du mot de passe d'un autre compte (ADR 0120).

Ajouter, supprimer et réinitialiser demandent `users.manage` et la
confirmation d'un administrateur dans la fenêtre de l'OS (mot de passe ou clé
d'accès). Gérer ses propres clés d'accès ne demande aucune capacité : le
navigateur demande la présence de l'utilisateur. La navigation de la fenêtre
n'est pas concernée par ces panneaux.

## Ce que l'app ne fait pas

- Elle n'a pas de magasin propre : tout ce qu'elle affiche vient de l'OS.
- Elle est mono-instance par utilisateur (`SingleInstancePerUser`) et n'a
  pas de `AutoStart`.
- Elle est système (`IsSystemApp`, ADR 0048) : non désinstallable.

## Liens

- Paramètres en fichiers et couches : [`settings-files.md`](../framework/settings-files.md), [`sensitive-settings.md`](sensitive-settings.md).
- Associations : [`user-file-associations.md`](user-file-associations.md).
- Bureau : [`desktop.md`](desktop.md). Accessibilité : [`accessibility.md`](accessibility.md).
- Thèmes : [`theme-editor.md`](theme-editor.md). Sauvegarde : [`system-backup.md`](system-backup.md).
- Synchronisation : [`synchronization.md`](synchronization.md). Mémoire : [`oom-killer.md`](oom-killer.md).
- Installation des apps : [`app-installation.md`](app-installation.md).
