> [English version](../../../../en/wasm/dev/adr/0219-app-authors-signing-tool.md)

# ADR 0219 : outil de signature des paquets pour les auteurs d'apps

## Statut

Accepté le 2026-10-31, chantier `todo/25-app-signing-tool` (série `todo/base`,
issue `TODO` [#321](https://github.com/Kazo-ca/HackerOs/issues/321)).
Complète l'[ADR 0214](0214-hack-store-hot-add-signing-transactions.md) §2 pour
les sources `github:` ; ne change rien à la vérification faite par l'OS.

## Contexte

L'ADR 0214 §2 accepte un champ `signature` (ECDSA P-256, SHA-256, IEEE P1363 en
base64) pour chaque version de l'`index.json` d'un dépôt d'app, et des clés
publiques dans `[signing-keys]` de `/etc/hackeros/app-sources.config`. Le
catalogue officiel signe déjà ses paquets (`HackerOs.MarketPlace.Tool keygen` /
`sign`, secret `HACK_STORE_SIGNING_KEY`). Un auteur qui publie depuis son propre
dépôt (`github:<owner>/<repo>`, branche `hackeros-packages`) n'a aucun outil :

- `HackerOsPack` (ADR 0182 §2) écrit le paquet et `index.json`, sans signature ;
  pire, `AppPackageWriter.AddToIndex` récrit l'index **sans** les signatures
  déjà présentes.
- Le modèle `hackeros-solution` (ADR 0207) a un workflow qui soumet l'app au
  catalogue, mais aucun ne pousse la branche `hackeros-packages`.

## Décision

### 1. Outil : l'outillage de build HackerOS (Q1 A)

Le générateur de manifestes (`HackerOs.Tools.ManifestGenerator`, livré dans le
paquet `HackerOs.App.Build` et utilisé par `HackerOsPack`) gagne deux commandes,
au même format que l'outil du catalogue :

- `keygen --private-key <fichier.pem>` : crée une paire P-256, écrit la clé
  privée PKCS#8 dans un **nouveau** fichier (droits `0600` hors Windows ; un
  fichier existant n'est jamais remplacé) et affiche la clé publique
  (SubjectPublicKeyInfo en base64) et la ligne `[signing-keys]` à donner aux
  administrateurs.
- `sign --package <fichier.hackapp> [--private-key <fichier.pem>]` : affiche la
  signature d'un paquet (clé lue dans `HACKEROS_APP_SIGNING_KEY` sans
  `--private-key`).

Cibles MSBuild de `HackerOs.Apps.targets` :

- `dotnet build -t:HackerOsKeyGen -p:HackerOsSigningKeyFile=<fichier.pem>` :
  lance `keygen`. La propriété est obligatoire (`HOSAPP0061`) : aucun chemin par
  défaut, pour qu'une clé ne soit jamais créée dans le dépôt par accident.
- `HackerOsPack` signe chaque paquet quand la variable d'environnement
  `HACKEROS_APP_SIGNING_KEY` contient la clé privée (PEM), ou quand
  `HackerOsSigningKeyFile` pointe sur le fichier. La clé ne passe jamais par la
  ligne de commande. La signature est vérifiée avec la clé publique avant
  d'être écrite dans `index.json`.

Les nouvelles cibles demandent la version de HackerOS qui contient ce chantier
(la prochaine après 2.2.0) : un dépôt d'app sur 2.2.0 garde le comportement
actuel.

### 2. Index : les signatures sont gardées

`AppPackageWriter.AddToIndex` écrit le champ `signature` de chaque version qui
en a une : republier une version ou en ajouter une ne perd plus les signatures
des autres. Une version republiée sans clé perd la sienne (ses octets ont pu
changer).

### 3. Branche `hackeros-packages` : nouveau workflow (Q2 A)

Le modèle `hackeros-solution` (et donc `Kazo-ca/HackerOs-AppTemplate`) gagne
`.github/workflows/publish-packages.yml`, sur un tag `v*` :

1. vérifie le sous-module et le tag (= version du manifeste), comme le workflow
   du Store ;
2. récupère la branche `hackeros-packages` (ou en crée une orpheline) dans un
   dossier, pour garder les versions déjà publiées ;
3. lance `HackerOsPack` avec `HackerOsPackageOutput` sur ce dossier et
   `HACKEROS_APP_SIGNING_KEY` venant du secret du même nom ;
4. valide et pousse la branche. C'est le seul workflow du modèle avec
   `contents: write`.

`hackpkg install <owner>/<repo>` fonctionne alors sans passer par le Store.

### 4. Sans secret : publié non signé, avec un avertissement (Q3 A)

Sans `HACKEROS_APP_SIGNING_KEY`, le paquet est publié avec son SHA-256
seulement et le workflow écrit un avertissement (`::warning::`). C'est la règle
de l'ADR 0214 §2.4 : une source sans clé dans `[signing-keys]` est acceptée.
Dès qu'un administrateur a ajouté la clé de l'auteur, un paquet non signé de ce
dépôt est refusé (`apps.package.unsigned`).

### 5. Nom du secret : `HACKEROS_APP_SIGNING_KEY` (Q4 A)

Distinct de `HACK_STORE_SIGNING_KEY` (catalogue officiel) : la clé d'un auteur
ne protège que ses dépôts.

### 6. Clé de l'auteur chez les administrateurs

L'auteur publie sa clé publique (README du dépôt, page du projet). Un
administrateur l'ajoute avec `sudo nano /etc/hackeros/app-sources.config` :

```text
[signing-keys]
github.com/contoso=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…
```

`github.com/<owner>` couvre tous les dépôts de l'auteur,
`github.com/<owner>/<repo>` un seul. Pour changer de clé sans coupure, l'auteur
crée la nouvelle paire, la fait ajouter après l'ancienne (séparées par une
virgule), change le secret, puis l'ancienne est retirée. Un hôte peut aussi
compiler la clé avec `HackerOsAppSigningKeys`.

## Conséquences

- Un auteur signe avec ce qu'il a déjà (`dotnet build`), rien à installer.
- Le catalogue officiel garde son propre outil (`HackerOs.MarketPlace.Tool`),
  au même format ; les deux ne partagent pas de code (le catalogue compile
  l'OS depuis son sous-module).
- `Kazo-ca/HackerOs-AppTemplate` reçoit le workflow quand il est régénéré sur
  la version qui contient ce chantier (PR en brouillon d'ici là).
- La clé privée ne quitte jamais la machine de l'auteur, sauf dans le secret
  de son dépôt ; aucun agent ne la manipule.

## Questions posées à Benoit

Posées le 2026-10-31 ; Benoit a choisi les recommandations (A) le même jour, telles que construites.

| Question | Choix | Réponse de Benoit (2026-10-31) |
|---|---|---|
| Q1 Où mettre `keygen` et `sign` | **A outillage de build HackerOS (recommandé)** ; B outil `dotnet` séparé ; C outil du MarketPlace | A |
| Q2 Qui publie `hackeros-packages` | **A nouveau workflow `publish-packages.yml` (recommandé)** ; B dans le workflow du Store ; C à la main | A |
| Q3 Sans secret | **A publier non signé avec un avertissement (recommandé)** ; B échouer | A |
| Q4 Nom du secret | **A `HACKEROS_APP_SIGNING_KEY` (recommandé)** ; B `HACK_STORE_SIGNING_KEY` | A |

## Tâches

- [x] `keygen` et `sign` du générateur, signature dans `pack`.
- [x] Cibles `HackerOsKeyGen` et signature dans `HackerOsPack`.
- [x] `AddToIndex` garde les signatures.
- [x] Workflow `publish-packages.yml` du modèle `hackeros-solution`, `*.pem` ignoré.
- [x] Tests (`ManifestGenerator`, `AppPackageWriterTests`).
- [x] Documentation (`app-installation.md`, `framework/app-repository.md`).
- [ ] `Kazo-ca/HackerOs-AppTemplate` régénéré sur la version qui contient ce chantier.
