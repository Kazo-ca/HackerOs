> [English version](../../../../en/wasm/dev/adr/0053-hack-environment-async-directory-change.md)

# ADR 0053 : Changement asynchrone du répertoire courant et lecture de l'environnement de connexion

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 3 — Shell, commandes et corbeille
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.2).
- **Remplace en partie :** ADR 0050, section 6, pour un seul point : le
  mutateur `HackEnvironment.CurrentDirectory { set; }`. Le reste de la
  section 6 et toute la section 5 restent en vigueur.
- **Précise :** ADR 0050, sections 5 et 6, sur le moment où
  `/etc/environment` et `/etc/hostname` sont lus.

## Contexte

L'ADR 0050 donne à `HackEnvironment.CurrentDirectory` un accesseur `set` qui
lève `HackDirectoryNotFoundException` si le chemin n'existe pas. Vérifier
l'existence demande un `stat` dans le système de fichiers. Dans HackerOS, tout
accès au système de fichiers est asynchrone (IndexedDB dans le navigateur) et
l'API `Hack*` de l'ADR 0043 n'expose que des méthodes `…Async`. Un accesseur
`set` synchrone devrait soit bloquer le seul fil du navigateur (impossible en
WebAssembly), soit accepter un chemin sans le vérifier, ce qui contredit l'ADR.

L'ADR 0050 dit aussi que l'environnement de connexion est « construit à
l'ouverture de session » et que `MachineName` est la première ligne de
`/etc/hostname`, sans dire quand ces fichiers sont lus.

## Décision

1. `HackEnvironment.CurrentDirectory` est en **lecture seule**. Le changement
   passe par :

   ```csharp
   public static Task SetCurrentDirectoryAsync(string path, CancellationToken cancellationToken = default);
   ```

   Le chemin peut être absolu, relatif au répertoire courant ou commencer par
   `~`. Un chemin absent lève `HackDirectoryNotFoundException` ; un fichier lève
   `HackIOException` (`NotDirectory`) ; un refus lève
   `HackUnauthorizedAccessException`. Comme `chdir`, la vérification passe par
   la passerelle de fichiers de l'app, avec ses capacités et ses droits.

2. `/etc/environment` et `/etc/hostname` sont lus **une fois par session**, au
   premier lancement d'un processus sans parent de cette session, par
   `LoginEnvironmentProvider`, en tant que root (comme `pam_env` pendant la
   connexion). Une modification de ces fichiers s'applique à la session
   suivante, pas aux processus déjà lancés. Pour `/etc/hostname`, c'est fidèle à
   Linux, où le nom d'hôte du noyau est lu au démarrage et ne suit pas le
   fichier.

## Conséquences

- Pas d'appel bloquant ni de vérification manquante dans le SDK.
- Les commandes `cd` (chantier `3-shell-commands`) appellent
  `SetCurrentDirectoryAsync` ou passent par l'accès contrôlé au shell
  (ADR 0051), qui fait la même vérification.
- Changer le nom de la machine demande une nouvelle session, comme un
  redémarrage sous Linux.

## Références

- ADR 0043 (API `Hack*`), ADR 0050 (environnement de processus), ADR 0051.
- [`process-environment.md`](../process-environment.md).
