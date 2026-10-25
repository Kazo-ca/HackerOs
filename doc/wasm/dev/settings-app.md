# App Paramètres (`org.hackeros.settings`)

## Objet

Décrit l'app système `Apps/System/HackerOs.Apps.Settings` : le panneau de
configuration de l'OS. Elle ne possède aucune donnée : chaque onglet lit et
écrit des fichiers de l'OS (paramètres en fichiers, ADR 0044) ou appelle une
façade `Hack*` du SDK. Les décisions qui l'ont façonnée sont l'ADR 0047
(manifeste), l'ADR 0048 (app système), l'ADR 0096 (arguments `--app=`), les
ADR 0105, 0120, 0121, 0160 et les sections 8 à 11 de l'ADR 0056.

Fichiers clés : `SettingsApp.cs` (classe `[AppManifest]`, identifiant, capacités,
poids de ressources), `SettingsWindow.razor` et `SettingsWindow.razor.cs` (la
fenêtre, un `MudTabs`), `SettingsWindow.Mobile.cs` (préférences mobiles),
un panneau `*Panel.razor` par onglet, `Strings.cs` et `Resources/` (textes
anglais et français). Le `manifest.json` est généré au build à partir de
`SettingsApp.cs` (ne pas l'éditer). Tests : `Tests/HackerOs.Apps.Settings.Tests`.

## Onglets

L'ordre compte : les arguments `--page=` et les crochets d'index s'y appuient.

| Onglet | Panneau | Contenu principal |
|---|---|---|
| Apparence | (dans `SettingsWindow.razor`) | Couche d'écriture (utilisateur ou système), thème du bureau et du mobile, accent, animations, thèmes utilisateur (`/etc/hackeros/themes` et `~/.config/hackeros/themes`, `UserThemeLibrary`) ; écrit `appearance.config`. |
| Bureau | `DesktopPanel` | Options et disposition du bureau (`desktop.config`, ADR 0160). |
| Accessibilité | `AccessibilityPanel` | Contraste élevé, taille du texte, mouvement réduit (ADR 0105). |
| Langue | `LanguagePanel` | Langue de l'interface. |
| Associations de fichiers | `FileAssociationsPanel` | Applications par défaut, couche système ou utilisateur (`HackFileAssociationLayer`, ADR 0121). |
| Autorisations accordées | (dans la fenêtre) | Capacités accordées à l'utilisateur. |
| Permissions | `AppPermissionsPanel` | Décisions par app et approbations des capacités dangereuses (ADR 0049). |
| Applications installées | `InstalledAppsPanel` | Liste des apps et de leurs commandes, désinstallation et réinstallation (ADR 0048). |
| Composants de l'OS | `OsComponentsPanel` | Fournisseur choisi pour chaque contrat (`HackComponentProviders.GetOverviewAsync`, `SetProviderAsync`). |
| Menus des fichiers | `FileMenuEntriesPanel` | Entrées de menu inscrites par les apps (ADR 0151). |
| Connexion au serveur | `ServerRelayPolicyPanel` (+ la fenêtre) | Serveur optionnel, relais réseau (ADR 0102, 0134). Visible dans tous les cas, l'état vient de `HackServerConnection`. |
| Synchronisation | (dans la fenêtre) | État de la synchro et conflits (ADR 0101). |
| Utilisateurs | `UsersPanel` | Mots de passe, réinitialisation par un administrateur (ADR 0120). |
| Mémoire | `MemoryPanel` | Fichier `/etc/hackeros/memory.config`, section `[oom-killer]` (ADR 0126). |
| Sauvegarde | `BackupPanel` | Présent seulement si `HackSystemBackup.IsSupportedFor` le permet (ADR 0099). |
| À propos | (dans la fenêtre) | Autorité, redémarrage (`HackSystem.RebootAsync`), zone de danger : réinitialisation d'usine (`HackSystem.FactoryResetAsync`), confirmée par la saisie de `RESET`. |
| Application | `AppDetailsPanel` | Ajouté seulement avec `--app=<id>` (ADR 0096) : détails et réglages d'une app. |

## Arguments de lancement

- `--page=appearance` ou `--page=desktop` : ouvre cet onglet (le « Changer le
  fond » et « Réglages du bureau » du bureau l'utilisent, ADR 0160 §8).
- `--app=<id>` : ouvre l'onglet « Application » de cette app ; un identifiant
  vide est ignoré, le dernier gagne.

Une valeur inconnue de `--page=` est ignorée.

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
