> [English version](../../../../en/wasm/dev/adr/0182-install-apps-from-github.md)

# ADR 0182 : installer une app depuis son dépôt GitHub au terminal

## Statut

Accepté le 2026-10-28 par Benoit, qui a choisi l'option recommandée aux quatre
questions (section « Réponses ») : vérification des API à l'installation,
branche `hackeros-packages`, seuls les dépôts de `Kazo-ca` de confiance par
défaut, installation pour la machine par un administrateur. Demande du même
jour : « on va juste faire une commande dans le terminal pour installer une
application à partir de son GitHub repository. Donc on doit s'assurer
d'autoriser GitHub comme host de confiance. » Complète l'[ADR 0176](0176-app-repository-template.md)
(dépôt template d'apps), l'[ADR 0048](0048-system-apps-and-uninstall.md)
(`apps.config`), l'[ADR 0049](0049-dangerous-capabilities-and-admin-approval.md)
(capacités dangereuses) et l'[ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
(versions du SDK et contrôles du manifeste).

## Contexte

- Aujourd'hui, une app n'entre dans l'OS qu'à la compilation d'un hôte :
  `BuiltInApplications.json` liste les apps, leurs assemblies partent dans
  `_framework` et sont chargées à la demande (`BlazorWebAssemblyLazyLoad`).
  « Installer » et « désinstaller » (ADR 0048) ne font qu'activer ou retirer
  une app déjà compilée dans l'hôte.
- Le dépôt template `Kazo-ca/HackerOs-AppTemplate` (ADR 0176) produit une
  solution .NET : une app fenêtrée, ses ressources `wwwroot/` déclarées dans
  `Assets` (avec leur SHA-256), son `manifest.json` généré.
- Chaque app tierce vit dans son propre dépôt GitHub (par exemple
  `Kazo-ca/HackerOs-App-Webamp`, `Kazo-ca/HackerOs-App-IRC`). Une app qui
  emballe un projet JavaScript le fait comme ressources d'une app .NET.
- L'OS tourne sans serveur sur GitHub Pages (`hackos.kazo.ca`). Le réseau réel
  d'une app passe par le proxy du serveur optionnel et la capacité dangereuse
  `network.real.access` ; sans serveur, il n'existe pas.
- Vérifié le 2026-10-28 avec un en-tête `Origin` :
  `raw.githubusercontent.com` et `cdn.jsdelivr.net` répondent
  `Access-Control-Allow-Origin: *` ; le téléchargement d'un fichier de release
  (`github.com/<o>/<r>/releases/download/...`) répond `302` **sans** cet en-tête,
  donc un `fetch` du navigateur échoue ; `api.github.com` limite les appels sans
  jeton à 60 par heure et par adresse IP.
- La publication de l'hôte est élaguée (`PublishTrimmed`, `TrimMode=partial`,
  [ADR 0113](0113-trimming-implementation-adjustments.md)) : les assemblies
  HackerOS et des apps restent entières, mais le framework .NET est élagué. Une
  assembly arrivée après la publication peut appeler une méthode du framework
  qui n'existe plus.
- Une assembly .NET chargée dans la page partage le runtime de l'OS : elle peut
  contourner le SDK par réflexion. La confiance dans la source est donc la vraie
  barrière, pas les capacités.

## Décision

Les points marqués **(Qn)** suivent la réponse de Benoit à la question Qn.

### 1. Le paquet d'app

Un **paquet d'app HackerOS** est une archive zip d'extension `.hackapp` :

```text
hackeros-package.json      identité, version, SDK visé, liste des fichiers et SHA-256
manifest.json              manifeste généré (ADR 0010, ADR 0047)
lib/<Assembly>.dll         assemblies de l'app (IL, pas WebCIL)
wwwroot/...                ressources déclarées dans Assets
```

`hackeros-package.json` :

```json
{
  "appId": "org.kazo.webamp",
  "version": "1.0.0",
  "sdkVersion": "2.1.0",
  "files": { "lib/Kazo.Webamp.dll": "sha256-…", "manifest.json": "sha256-…" }
}
```

La cible MSBuild `HackerOsPack` (paquet `HackerOs.App.Build`) produit le paquet
après `dotnet publish -c Release`. Le workflow CI du modèle `hackeros-solution`
(ADR 0176 §1) l'appelle à chaque tag `v*`.

### 2. Où l'OS trouve le paquet **(Q2)**

Le workflow CI pousse le paquet sur une branche dédiée du dépôt de l'app,
`hackeros-packages`, sans historique de code :

```text
index.json                 versions publiées, la plus récente en premier, SHA-256 de chaque paquet
1.0.0/org.kazo.webamp-1.0.0.hackapp
```

L'OS lit `https://raw.githubusercontent.com/<owner>/<repo>/hackeros-packages/index.json`
puis le paquet. Pas d'appel à `api.github.com` (limite de 60 appels par heure),
pas de release GitHub (bloquée par CORS dans le navigateur). Une release GitHub
peut porter le même fichier pour les humains ; l'OS ne la lit pas.

### 3. GitHub comme source de confiance **(Q3)**

Nouveau fichier machine `/etc/hackeros/app-sources.config`
(`system:system 0644`, grammaire `.config` de l'ADR 0011) :

```text
# Hôtes que l'OS contacte lui-même pour télécharger des paquets d'app.
[hosts]
github=raw.githubusercontent.com

# Propriétaires GitHub dont les apps s'installent sans avertissement.
[trusted]
github.com/Kazo-ca=true
```

- Le téléchargement est fait **par l'OS** (service système), directement par le
  `fetch` du navigateur, et seulement vers un hôte listé dans `[hosts]`. Il ne
  passe ni par le réseau simulé ni par le proxy, et ne demande pas
  `network.real.access` : aucune app ne reçoit d'accès réseau.
- Un dépôt dont le propriétaire n'est pas dans `[trusted]` est refusé, sauf avec
  `--allow-untrusted` : un administrateur confirme alors un avertissement qui
  dit que le code de l'app tourne avec les mêmes droits que l'OS.
- Le fichier est créé par une migration au démarrage avec ces valeurs, commenté ;
  un administrateur l'édite avec `sudo nano`.

### 4. La commande `hackpkg` **(Q4)**

Commande du catalogue `org.hackeros.commands.hackpkg` :

```text
hackpkg install <source>[@<version>] [--allow-untrusted]
hackpkg remove <appId>
hackpkg update [<appId>]
hackpkg list
hackpkg info <source>
hackpkg sources
```

`<source>` accepte `github:Kazo-ca/HackerOs-App-Webamp`,
`Kazo-ca/HackerOs-App-Webamp` et `https://github.com/Kazo-ca/HackerOs-App-Webamp`.
Sans version, la plus récente de `index.json`.

Installer, retirer et mettre à jour sont des opérations **machine** : `sudo
hackpkg install ...`, ou l'agent d'authentification de l'OS (ADR 0055 §5).
`list`, `info` et `sources` sont permis à tous.

### 5. Étapes de l'installation

1. Lire `index.json`, choisir la version, télécharger le paquet ; vérifier son
   SHA-256 contre `index.json`, puis chaque fichier contre
   `hackeros-package.json`.
2. Refuser si `appId` est celui d'une app intégrée ou d'une app déjà installée
   depuis une autre source ; si `sdkVersion` a un autre majeur que le SDK de
   l'OS ou un mineur plus grand (`apps.package.sdk-incompatible:<v>`) ; si le
   manifeste ne passe pas les contrôles de l'ADR 0098.
3. **(Q1)** Vérifier chaque référence de membre des assemblies (méthodes,
   champs, types) contre le runtime chargé, par `System.Reflection.Metadata` :
   une API élaguée de l'OS refuse l'installation avec la liste des membres
   manquants (`apps.package.missing-api:<membre>`).
4. Afficher les capacités demandées ; les capacités dangereuses passent par
   l'approbation de l'ADR 0049, avant toute écriture.
5. Écrire les fichiers dans `/usr/lib/hackeros/apps/<appId>/<version>/`
   (`root:root`, dossiers `0755`, fichiers `0644`) et la source dans
   `/etc/hackeros/apps.config`, nouvelle section :

   ```text
   [installed]
   org.kazo.webamp=github:Kazo-ca/HackerOs-App-Webamp@1.0.0
   ```

6. Ajouter l'app au catalogue en cours de session (lanceur, recherche,
   « Ouvrir avec », terminal) ; journaliser dans `/var/log/syslog`
   (`apps: org.kazo.webamp 1.0.0 installed from github:Kazo-ca/HackerOs-App-Webamp (by=alice)`).

Au démarrage, `AppInstallationService` relit `[installed]` et inscrit ces apps
au catalogue après les apps intégrées. Au lancement, l'assembly est lue depuis
`/usr/lib/hackeros/apps/...` et chargée par `Assembly.Load(byte[])` au lieu du
chargement paresseux de Blazor ; les ressources sont servies par
`AppAssetStore` depuis le même dossier.

`hackpkg remove` arrête l'app (ADR 0048), retire la ligne de `[installed]` et
le dossier. Les réglages de l'utilisateur (`~/.config/<appId>/`) restent, comme
pour une désinstallation. `[uninstalled]` garde son sens : il retire une app
intégrée sans rien effacer.

### 6. Suite : le HackerOs Store

Hors de cette ADR. Une future app système « HackerOs Store », façon Play Store
ou Microsoft Store, lira la liste du dépôt `Kazo-ca/HackerOs-AppMarketPlace` et
installera par le même service que `hackpkg`. Note dans
[`../plans/hackeros-store.md`](../plans/hackeros-store.md), à reprendre quand des
apps seront publiées.

## Réponses

Questions posées à Benoit le 2026-10-28 ; il a choisi **A** aux quatre.

- **Q1. Framework élagué.** A (recommandé) : vérification des références à
  l'installation, refus avec la liste des API manquantes. B : l'hôte officiel
  n'élague plus le framework (toutes les apps marchent, quelques Mio de plus à
  télécharger pour tout le monde). C : A maintenant, B si trop d'apps sont
  refusées.
- **Q2. Où publier le paquet.** A (recommandé) : branche `hackeros-packages` lue
  par `raw.githubusercontent.com`. B : GitHub Pages du dépôt de l'app (une seule
  version, et la page du dépôt est prise). C : releases GitHub (marche seulement
  avec le serveur, par le proxy). D : jsDelivr (`cdn.jsdelivr.net/gh/...`), un
  service tiers entre GitHub et l'OS.
- **Q3. Confiance.** A (recommandé) : seuls les dépôts de `Kazo-ca` sans
  avertissement, les autres avec `--allow-untrusted` et un administrateur. B :
  tout GitHub sans avertissement. C : aucun par défaut, l'administrateur ajoute
  les propriétaires.
- **Q4. Portée.** A (recommandé) : installation pour la machine seulement,
  administrateur requis (comme `apt`). B : aussi par utilisateur
  (`~/.local/share/hackeros/apps`), sans administrateur ; tout utilisateur peut
  alors faire tourner du code tiers dans le runtime partagé.

## Conséquences

- Une app peut être ajoutée à un OS déjà publié, sans recompiler l'hôte.
- Le code installé n'est pas isolé de l'OS ; la liste des sources de confiance
  et l'approbation d'un administrateur en tiennent lieu. Une isolation (app en
  Web Worker, [ADR 0070](0070-worker-hosted-processes.md)) reste possible plus tard.
  Le JavaScript d'une app (ressources `wwwroot/`) tourne lui aussi dans l'origine
  de l'OS et peut lire son IndexedDB, sauf s'il est servi dans un iframe isolé.
- Les paquets ne sont pas signés : les SHA-256 d'`index.json` protègent d'un
  téléchargement abîmé, pas d'un dépôt compromis. La signature reste à décider
  avec le Store.
- Deux origines d'apps dans le catalogue : intégrées (build) et installées
  (`/usr/lib/hackeros/apps`). Les vues qui listent les apps doivent montrer la
  source.
- Le modèle `hackeros-solution` gagne un workflow de publication ; un dépôt
  d'app créé avant doit l'ajouter.
- La sauvegarde du système ([ADR 0099](0099-os-backup-and-low-storage.md))
  inclut `/usr/lib/hackeros/apps`.

## Tâches

- [x] Réponses de Benoit à Q1 à Q4 (A partout, 2026-10-28).
- [ ] Cible `HackerOsPack` dans `HackerOs.App.Build` et workflow de publication dans `hackeros-solution`.
- [ ] `app-sources.config` (lecture, migration de création).
- [ ] Section `[installed]` dans `AppsConfigDocument` ; inscription au catalogue en cours de session.
- [ ] Chargement d'une assembly installée depuis le système de fichiers, ressources par `AppAssetStore`.
- [ ] Vérification des références contre le runtime (Q1).
- [ ] Commande `hackpkg` et sa doc (`linux-commands.md`, `app-installation.md`).
- [ ] Tests unitaires (lecture des fichiers, contrôles du paquet) et test de bout en bout avec un paquet de test servi localement.
