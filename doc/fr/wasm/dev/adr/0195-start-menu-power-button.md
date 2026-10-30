> [English version](../../../../en/wasm/dev/adr/0195-start-menu-power-button.md)

# ADR 0195 : bouton Marche/arrêt du menu Démarrer

## Statut

Accepté le 2026-10-30. Demande de Benoit du 2026-10-30 (fil « Déplacer Power dans
le menu Démarrer »). Issue de suivi pour « Changer d'utilisateur » :
[#290](https://github.com/Kazo-ca/HackerOs/issues/290).

- **Complète :** [ADR 0187](0187-guest-start-and-sign-in-credits.md) (compte
  invité et son affichage dans le menu Démarrer),
  [ADR 0162](0162-restart-after-a-host-build-change.md) (redémarrage après une
  mise à jour), [ADR 0099](0099-os-backup-and-low-storage.md) §3
  (`RESET_CACHE` du service worker).
- **Ne change pas :** le redémarrage administrateur de l'ADR 0056 §11
  (`HackSystem`, capacité `system.power`).

## Contexte

- La barre des tâches avait un bouton « Power » à droite de l'horloge. Il
  ouvrait la boîte « Se déconnecter de HackerOS ».
- Le menu Démarrer affiche déjà, pour tout utilisateur, son nom et son
  identifiant en haut à droite ; le compte invité y ajoute « Créer mon compte »
  (ADR 0187 §5).
- Benoit veut le bouton Power dans le menu Démarrer, avec trois choix :
  Déconnexion, Changer d'utilisateur (grisé pour l'invité) et Redémarrer.
  Redémarrer a le même effet que Déconnexion, suivi d'un rechargement complet
  (« hard refresh »).
- L'OS n'a qu'une session à la fois (`ISessionService.CurrentPrincipal`).

## Décision

### 1. Le bouton dans le menu Démarrer

- La colonne de droite du menu Démarrer (« Accès rapide ») se termine par le
  bouton **Marche/arrêt** (icône `power`). Il ouvre un menu (`role="menu"`) au-dessus
  de lui : **Se déconnecter**, **Changer d'utilisateur**, **Redémarrer**.
- Clavier : le premier choix prend le focus ; Haut, Bas, Début et Fin passent
  d'un choix actif à l'autre ; Échap ferme le menu Power d'abord (le focus revient
  au bouton), puis le menu Démarrer.
- Sur un petit écran, le bouton reste visible et occupe toute la largeur de la
  colonne.
- La barre des tâches de HackerOS n'a plus de bouton de session. Le paquet
  `HackerOs.Taskbar.Blazor` garde `ITaskbarSessionCommands` pour les autres hôtes.
- L'utilisateur reste affiché en haut du menu Démarrer, comme avant, pour tout
  compte.

### 2. Se déconnecter

Ouvre la boîte de déconnexion existante (liste des processus, confirmation). La
confirmation arrête les apps de la session, ferme la session et ouvre l'écran de
connexion.

### 3. Redémarrer

- Même boîte, intitulée « Redémarrer HackerOS », qui dit que le système sera
  rechargé depuis le serveur. La confirmation ferme la session comme une
  déconnexion.
- Ensuite l'hôte navigateur affiche l'écran de démarrage, vide les caches qui
  servent la copie de l'OS (`hostRestart.js`), puis recharge la page avec
  `SystemPowerService.RestartAfterAsync` (journaux vidés), comme après une mise à
  jour (ADR 0162) :
  - avec le service worker de la PWA, il envoie `RESET_CACHE` (ADR 0099 §3) : la
    version est téléchargée de nouveau ; hors ligne ou serveur injoignable, le
    cache est gardé et l'OS redémarre quand même ;
  - sans service worker (développement local), il supprime les caches
    `dotnet-resources*` de .NET.
- Aucune autorisation administrateur : rien n'est effacé, c'est la session de
  l'utilisateur qui se ferme.
- La base de données de l'OS (IndexedDB) n'est jamais touchée.

### 4. Changer d'utilisateur

- Affiché mais **grisé** pour l'instant, avec « Bientôt disponible ».
- Toujours grisé pour l'invité (« Indisponible en session invité ») : l'invité
  n'existe que quand aucun autre compte n'existe (ADR 0187), et il passe par
  « Créer mon compte ».
- Le faire demande plusieurs sessions ouvertes en même temps, ce que l'OS ne sait
  pas faire : `ISessionService`, les fenêtres, les processus, le cycle de vie des
  apps et les préférences vivantes supposent une session unique. Le travail est
  listé dans l'issue [#290](https://github.com/Kazo-ca/HackerOs/issues/290)
  (étiquette `TODO`).

### 5. Thèmes

Nouveaux jetons du menu Démarrer, dans le catalogue des jetons (éditeur de
thèmes) : `--hos-launcher-power-background`, `--hos-launcher-power-color`,
`--hos-launcher-power-hover-background`. `themes.css` ajoute aussi
`--hos-launcher-power-hover-color`, `-border`, `-radius` et `-shadow`. Chaque
thème intégré les définit : bouton en relief de Windows 98, bouton orange
« Arrêter » de Windows XP, bouton vitré de Windows 7, bouton plat de Windows 10,
rouge discret de macOS et iOS, orange d'Ubuntu, pilule d'Android, noir et blanc du
contraste élevé.

## Conséquences

- Un clic de plus pour se déconnecter, mais la barre des tâches a plus de place
  pour les fenêtres, comme sur Windows.
- L'interface mobile n'a toujours pas de déconnexion : hors de cette ADR.
- La boîte de déconnexion s'affiche sur l'écran 1, même si le menu Démarrer
  était ouvert sur un autre écran.
- Test de bout en bout : `StartMenuPowerTests`.
