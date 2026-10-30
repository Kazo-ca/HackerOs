> [English version](CHANGELOG.md)

# Journal des changements

Les changements de HackerOS, par version. Ce fichier est généré à partir de `wasm2/HackerOs/changes/` par
`wasm2/HackerOs/build/tools/collect-changes.sh` (ADR 0186) : ne pas le modifier à la main. Les changements
déjà déployés mais pas encore publiés sont dans la page Paramètres ▸ Nouveautés de l'OS.

## 2.1.0 (2026-10-26)

### Nouveau

- **Avis quand une nouvelle version est disponible.** Les sessions ouvertes cherchent une nouvelle version toutes les 5 minutes et affichent un bandeau qui propose de redémarrer pour l'utiliser. ([#217](https://github.com/Kazo-ca/HackerOs/pull/217), [0174-announce-a-new-version-to-open-sessions.md](doc/fr/wasm/dev/adr/0174-announce-a-new-version-to-open-sessions.md))
- **Un administrateur ajoute et supprime des comptes.** Un administrateur peut créer un compte ou supprimer un compte existant dans Paramètres ▸ Utilisateurs. ([#211](https://github.com/Kazo-ca/HackerOs/pull/211), [0169-administrator-adds-and-removes-users.md](doc/fr/wasm/dev/adr/0169-administrator-adds-and-removes-users.md))
- **Connexion par clé d'accès.** Ajoutez une clé d'accès à votre compte dans Paramètres ▸ Utilisateurs et connectez-vous avec Windows Hello, Touch ID ou une clé de sécurité. La clé d'accès déverrouille aussi votre trousseau. ([#211](https://github.com/Kazo-ca/HackerOs/pull/211), [0168-passkey-sign-in.md](doc/fr/wasm/dev/adr/0168-passkey-sign-in.md))

### Amélioré

- **Explorateur de fichiers et dialogues sur téléphone.** L'explorateur de fichiers a une disposition faite pour les téléphones, un toucher ouvre un fichier, et les dialogues de fichiers occupent tout l'écran sur mobile. ([#212](https://github.com/Kazo-ca/HackerOs/pull/212), [0171-mobile-file-explorer-and-full-screen-dialogs.md](doc/fr/wasm/dev/adr/0171-mobile-file-explorer-and-full-screen-dialogs.md))
- **Menu latéral réductible dans Paramètres.** Les pages de Paramètres sont listées dans un menu latéral qui se réduit aux icônes, et qui devient un bouton de menu sur un téléphone. ([#210](https://github.com/Kazo-ca/HackerOs/pull/210), [0170-settings-side-menu.md](doc/fr/wasm/dev/adr/0170-settings-side-menu.md))

### Corrigé

- **Alignement du menu Démarrer.** Le menu Démarrer est maintenant aligné sur son bouton de la barre des tâches. ([#212](https://github.com/Kazo-ca/HackerOs/pull/212), [0171-mobile-file-explorer-and-full-screen-dialogs.md](doc/fr/wasm/dev/adr/0171-mobile-file-explorer-and-full-screen-dialogs.md))

### Pour les développeurs

- **SDK 2.1 : clés d'accès, comptes, OpenOnTap et délai de grâce des tâches de fond.** Les ajouts au SDK depuis 2.0 sont publiés : API des clés d'accès et de l'administration des comptes, HackFileView.OpenOnTap pour les écrans tactiles, et BackgroundJobService.ParentExitGracePeriod. ([#217](https://github.com/Kazo-ca/HackerOs/pull/217))
- **Paquets publiés sur nuget.org à chaque version.** Chaque paquet HackerOs.*, avec ses symboles, est publié sur nuget.org et joint à la GitHub Release quand la version change sur main. ([#216](https://github.com/Kazo-ca/HackerOs/pull/216), [0173-automatic-releases-from-the-version-file.md](doc/fr/wasm/dev/adr/0173-automatic-releases-from-the-version-file.md))
