> [English version](../../../../en/wasm/dev/adr/0214-hack-store-hot-add-signing-transactions.md)

# ADR 0214 : Hack Store, ajout sans redémarrage, signature des paquets et installation transactionnelle

## Statut

Accepté le 2026-10-31, chantier `todo/19-hackstore-later` (issue `TODO`
[#251](https://github.com/Kazo-ca/HackerOs/issues/251), chantier 7 du plan du
Hack Store). Réalise la réponse B à la Q5 de
l'[ADR 0182](0182-install-apps-from-github.md) (« plus tard : ajout sans
redémarrage »), la signature laissée ouverte par l'ADR 0182 (« Conséquences »)
et la question 1 du plan `wasm-v3-completion-plan.md` (transaction, migration
des données). Change l'[ADR 0212](0212-hack-store-app-build.md) §3 : « Redémarrer
pour terminer » ne sert plus qu'aux apps déjà chargées. Cinq questions ont été
posées à Benoit (Q1 à Q5 ci-dessous) ; il a choisi la réponse A à chacune le
2026-10-31.

## Contexte

- Le catalogue des apps (`AppCatalog`) est construit une fois au démarrage, avec
  les apps intégrées puis les apps installées (ADR 0182 §5, ADR 0208). Environ
  quatre-vingts endroits le lisent ; presque tous relisent ses propriétés à
  chaque appel, deux registres de chargement paresseux en tirent une liste
  d'assemblies à la construction.
- Une assembly chargée dans `AssemblyLoadContext.Default` ne se décharge pas : une
  nouvelle version d'une app déjà lancée ne peut pas remplacer l'ancienne dans
  la même page.
- Les paquets ne sont protégés que par le SHA-256 que publie `index.json` ou
  `catalog.json` : un dépôt compromis peut publier un autre paquet avec son
  SHA-256.
- .NET dans WebAssembly n'a ni ECDSA ni RSA ; la vérification des clés d'accès
  passe déjà par Web Crypto (`webAuthn.js`, ADR 0168).
- `CommitAsync` écrit les fichiers, puis la ligne de `[installed]`, puis efface
  l'ancienne version : une coupure laisse l'ancienne version en place, mais aussi
  un dossier à moitié écrit, et rien ne permet de revenir à la version
  précédente ni de protéger les données d'un utilisateur d'une migration ratée.
- Le manifeste porte déjà `update.minimumUpgradableVersion` et
  `update.migrationIds`, que personne ne lit.

## Décision

### 1. Ajout à chaud (ADR 0182, Q5 B)

1. Après une installation, une mise à jour, un retrait ou un retour arrière
   réussi, l'OS relit les apps installées (comme `update-desktop-database` après
   `apt install`) et met à jour le catalogue en cours : `AppCatalog` garde son
   instance, son contenu est remplacé d'un seul coup (`AppCatalog.Revision`,
   évènement `AppCatalog.Changed`). Le menu Démarrer, l'écran d'accueil mobile, la
   recherche, « Ouvrir avec », le terminal (`PATH`) et les composants intégrables
   voient le changement tout de suite ; les registres de chargement paresseux
   apprennent les nouvelles assemblies.
2. **(Q1 A)** Une app dont une assembly est déjà chargée dans la page garde sa
   version jusqu'au redémarrage : sa mise à jour, son retrait ou son retour
   arrière attend « Redémarrer pour terminer » (ADR 0212 §3), qui ne sert plus
   qu'à elles. Une app jamais lancée depuis le démarrage change tout de suite.
3. Une app de service ajoutée à chaud n'est pas démarrée : elle démarre au
   prochain démarrage, comme les autres services.
4. Le rapport d'installation dit si l'app est prête (`HackPackageInstallReport.RestartRequired`) ;
   `hackpkg` affiche « disponible maintenant » ou « redémarrez pour terminer ».
   `HackInstalledPackage.Loaded` reste vrai après le retrait d'une app chargée :
   `hackpkg` et le Store disent alors de redémarrer.
5. Mise en œuvre : `InstalledAppsBoot.Live` est l'instance d'`AppCatalog` que
   tous les services reçoivent ; ce n'est jamais le catalogue intégré lui-même
   (`AppCatalog.CreateLiveCopy`). Après une écriture réussie, la passerelle
   appelle `IInstalledAppsActivator`, qui relit les apps installées
   (`InstalledAppsBoot.ReloadAsync`), garde la version chargée d'une app dont
   une assembly est dans `AssemblyLoadContext.Default`, remplace le contenu du
   catalogue (`AppCatalog.ReplaceContents`) et ajoute les nouvelles assemblies au
   registre de chargement paresseux (`BuildKnownAssemblyLoaderRegistry.AddKnownAssemblies`).
   `AppEnablementRegistry.Changed` relaie `AppCatalog.Changed`, et
   `AppInstallationService` relit ses fichiers à chaque changement.

### 2. Signature des paquets

1. **Algorithme** : ECDSA P-256 avec SHA-256, signature IEEE P1363 (`r||s`) en
   base64, sur les octets du fichier `.hackapp`. Clé publique au format
   SubjectPublicKeyInfo (DER) en base64. La vérification passe par Web Crypto
   dans le navigateur, par `System.Security.Cryptography.ECDsa` ailleurs
   (tests, serveur).
2. **Où** : champ facultatif `signature` de chaque version, dans `index.json`
   (dépôt d'app) comme dans `catalog.json` (catalogue).
3. **Clés** : nouvelle section `[signing-keys]` de
   `/etc/hackeros/app-sources.config`, une ligne par source de confiance,
   plusieurs clés séparées par des virgules pour en changer sans coupure :

   ```text
   [signing-keys]
   catalog:official=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…
   ```

   Les clés compilées dans l'hôte viennent de la propriété MSBuild
   `HackerOsAppSigningKeys` (comme les autres valeurs par défaut de l'ADR 0208) et
   restent exigées même si la ligne est retirée du fichier ; un administrateur
   peut en ajouter avec `sudo nano`. Une clé invalide est ignorée avec un
   avertissement.
4. **(Q2 A) Règle** : une source qui a au moins une clé exige une signature
   valide par l'une d'elles ; sinon le paquet est refusé
   (`apps.package.unsigned`, `apps.package.bad-signature`) avant toute demande
   d'administrateur. Une source sans clé est acceptée comme aujourd'hui (SHA-256
   seulement) ; la fenêtre d’approbation montre « Non signé (vérifié par son SHA-256 seulement) »,
   `hackpkg info` montre « Signature : aucune (SHA-256 seulement) ». Les clés
   d'une source `catalog:<nom>` sont celles de `catalog:<nom>` ; celles d'un dépôt
   `github:` sont celles de `github.com/<owner>` et de `github.com/<owner>/<repo>`.
5. **(Q3 A) Catalogue officiel** : l'outil `HackerOs.MarketPlace.Tool` du dépôt
   `Kazo-ca/HackerOs-AppMarketPlace` gagne `keygen` (crée la paire, écrit la clé
   privée PKCS#8 dans un fichier local, affiche la clé publique) et `sign` ; le
   workflow `publish.yml` signe chaque paquet avec le secret
   `HACK_STORE_SIGNING_KEY` quand il existe. Benoit crée la paire chez lui ; la
   clé privée ne passe jamais par un agent. Tant que la clé publique n'est pas
   dans `HackerOsAppSigningKeys`, rien n'est exigé.

### 3. Installation transactionnelle

Comme `dpkg` et son dossier `updates/` :

1. Avant d'écrire, l'OS pose un journal `<racine>/<appId>/.transaction`
   (`operation`, `version`, `previous`, `startedAt`), où `<racine>` est
   `/usr/lib/hackeros/apps` ou `~/.local/share/hackeros/apps`.
2. Les fichiers sont écrits dans `<racine>/<appId>/<version>.partial/`, puis le
   dossier est renommé `<version>/` en une seule opération.
3. La ligne de `[installed]` est le point de validation : tant qu'elle n'a pas
   changé, l'ancienne version reste celle de l'OS.
4. Ensuite seulement, l'OS efface ce qui n'est plus gardé (§4) et le journal.
5. **Reprise** : avant toute installation, tout retrait et à la commande
   `hackpkg repair`, l'OS relit le journal de l'app concernée (tous pour
   `repair`). Si `[installed]` désigne déjà la nouvelle version, il finit le
   ménage ; sinon il efface `.partial` et le dossier neuf et garde l'ancienne
   version. Une ligne de `/var/log/syslog` dit ce qui a été fait. Au démarrage,
   un dossier `.partial` ou une version non listée n'est jamais chargé.
6. `hackpkg repair` répare les apps de l'utilisateur ; `hackpkg repair --system`
   répare aussi celles du système, après une authentification d'administrateur.
   Il efface aussi les dossiers `.partial` sans journal et le dossier d'une app
   qu'aucune ligne de `[installed]` ne nomme plus.
7. `update.minimumUpgradableVersion` est appliqué : une version installée plus
   ancienne refuse la mise à jour en place (`apps.package.reinstall-required` :
   retirer puis installer).

### 4. Retour arrière et données des utilisateurs

1. **(Q4 A)** Une mise à jour garde la version qu'elle remplace (une seule) :
   `<racine>/<appId>/<précédente>/`. `hackpkg rollback <appId>` (et le bouton
   « Revenir à <version> » du Store, sur une app installée) remet
   cette version dans `[installed]`, avec les mêmes approbations qu'une
   installation de la même portée (validation en deux étapes pour le système ou
   une source non fiable, opération `AppInstallApprovalOperation.Rollback`), et
   garde à son tour la version quittée. Le retrait efface toutes les versions.
2. **(Q5 A) Sauvegarde et migration** : chaque utilisateur a
   `~/.config/hackeros/app-data-versions.config` (`[versions] <appId>=<version>`,
   la version qui a utilisé ses données en dernier). Au lancement d'une app
   installée dont la version diffère de celle notée, avant que le processus
   existe, l'OS :
   - copie `~/.config/<appId>/` et `~/.local/share/<appId>/` dans
     `~/.local/share/hackeros/app-backups/<appId>/<ancienne version>/` ;
   - si la nouvelle version est plus ancienne (retour arrière) et qu'une
     sauvegarde de cette version existe, remet cette sauvegarde à la place des
     données ;
   - sinon (mise à jour), donne au processus la variable
     `HACKEROS_PREVIOUS_VERSION=<ancienne version>` ; l'app migre elle-même ses
     données, comme `onUpgrade` sous Android ;
   - note la nouvelle version. Deux sauvegardes au plus sont gardées par app :
     celle de la version quittée et celle de la version lancée.

   La sauvegarde (`AppDataGuard`) copie `~/.config/<appId>/` dans
   `<sauvegarde>/.config/` et `~/.local/share/<appId>/` dans
   `<sauvegarde>/.local-share/`. Une sauvegarde ratée (disque plein) est écrite
   dans le journal et n'empêche pas le lancement ; la version n'est alors pas
   notée, et le lancement suivant réessaie.
3. Tout se fait avec les droits de l'utilisateur, dans son dossier : aucune
   donnée d'un autre utilisateur n'est lue. Aucun ajout au SDK n'est nécessaire
   (`HackEnvironment.GetEnvironmentVariable`).

### 5. SDK

`HackPackageInstallReport.RestartRequired` et `.Signed`,
`HackPackageInfo.SignatureRequired`, `HackInstalledPackage.PreviousVersion`,
`HackAppPackages.RollbackAsync` et `HackAppPackages.RepairAsync` dans
`PublicAPI.Unshipped.txt` (prochaine mineure). Les nouveaux membres de
`IAppManagementGateway` (`RollbackPackageAsync`, `RepairPackagesAsync`) ont une
implémentation par défaut qui lève `NotSupportedException`.

## Conséquences

- Une app installée depuis le Store ou `hackpkg` est utilisable tout de suite,
  sauf la mise à jour d'une app déjà lancée.
- Tout code qui garde une copie du catalogue doit écouter `AppCatalog.Changed` ;
  les vues qui listent les apps le font par `AppEnablementRegistry.Changed`, qui
  relaie désormais les changements du catalogue.
- Le catalogue officiel n'est vraiment protégé qu'une fois la clé créée par
  Benoit et compilée dans l'hôte (`OfficialCatalogSigningKey` ou
  `HackerOsAppSigningKeys`) ; les versions déjà publiées doivent être republiées
  pour recevoir leur signature avant cette version de l'OS.
- La signature d'un dépôt d'app (`github:`) est possible par le même format ; un
  outil pour les auteurs reste à faire (issue `TODO`
  [#321](https://github.com/Kazo-ca/HackerOs/issues/321)).
- Une mise à jour occupe deux versions sur le disque ; les sauvegardes de
  données comptent dans le quota de l'utilisateur.

## Questions posées à Benoit

| Question | Choix | Réponse de Benoit (2026-10-31) |
|---|---|---|
| Q1 Mise à jour d'une app déjà ouverte | A redémarrage pour celle-là, le reste à chaud ; B toute mise à jour attend le redémarrage | A |
| Q2 Source avec clé, paquet non signé | A refuser ; B avertir | A |
| Q3 Clé du catalogue officiel | A Benoit la crée avec `keygen`, signature dans `publish.yml` ; B plus tard (issue `TODO`) | A |
| Q4 Version précédente gardée | A oui, `hackpkg rollback` ; B non | A |
| Q5 Migration des données | A l'OS sauvegarde, l'app migre (`HACKEROS_PREVIOUS_VERSION`) ; B migrations nommées exécutées par l'OS | A |

## Tâches

- [x] Catalogue en direct (`Revision`, `Changed`), registres paresseux extensibles, relecture des apps installées après chaque changement.
- [x] Apps déjà chargées : changement gardé pour le redémarrage ; `RestartRequired`.
- [x] `[signing-keys]`, `HackerOsAppSigningKeys`, champ `signature`, vérification (Web Crypto, `ECDsa`).
- [x] Journal `.transaction`, dossier `.partial`, reprise, `hackpkg repair`, `minimumUpgradableVersion`.
- [x] Version précédente gardée, `hackpkg rollback`, bouton du Store.
- [x] Sauvegarde des données au lancement, `HACKEROS_PREVIOUS_VERSION`.
- [x] `keygen`, `sign` et signature dans `publish.yml` (dépôt AppMarketPlace).
- [x] Tests et documentation (`app-installation.md`, `linux-commands.md`, `sdk-reference.md`).
