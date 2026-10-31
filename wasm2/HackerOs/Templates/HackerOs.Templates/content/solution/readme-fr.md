> [English version](README.md)

# HackerOs.Template.Solution

Un dépôt d'app HackerOS, créé avec le modèle `hackeros-solution`.

| Dossier | Contenu |
| --- | --- |
| `src/HackerOs.Template.Solution` | L'app (app fenêtrée ; sa classe `[AppManifest]` génère `manifest.json`) |
| `src/HackerOs.Template.Solution.Os` | Un hôte qui lance tout HackerOS avec les apps de base et cette app : le projet à démarrer |
| `tests/HackerOs.Template.Solution.Tests` | Les tests de l'app |
| `external/HackerOs` | Le dépôt HackerOS en sous-module git, sur un tag de release |
| `store/` | Ce que montre le Hack Store : icône (PNG 256×256), descriptions courtes, descriptions, `store.json` |

## Premiers pas

```bash
# Un dépôt créé depuis le template GitHub a déjà le sous-module :
git clone --recurse-submodules <votre dépôt>

# Un dossier créé par « dotnet new hackeros-solution » l'ajoute une fois :
git init
git submodule add --depth 1 https://github.com/Kazo-ca/HackerOs.git external/HackerOs
git -C external/HackerOs checkout v<version de HackerOS>
```

Ouvrez ensuite le `.sln` dans Visual Studio ou Rider et démarrez `HackerOs.Template.Solution.Os`,
ou lancez `dotnet run --project src/HackerOs.Template.Solution.Os`.

## La version de HackerOS

`external/HackerOs/wasm2/HackerOs/build/Versions.props` est la version de HackerOS de ce dépôt.
Pour changer de version, placez le sous-module sur son tag et validez-le :

```bash
git -C external/HackerOs fetch --depth 1 origin tag v2.2.0
git -C external/HackerOs checkout v2.2.0
git add external/HackerOs
```

Ne modifiez jamais le sous-module : on ne modifie pas l'OS depuis un dépôt d'app, et la CI refuse
un sous-module modifié ou hors de son tag.

## Deux profils de build

| Profil | HackerOS vient de | Usage |
| --- | --- | --- |
| `Debug` | les projets du sous-module (`HackerOsUseSource=true`) | entrer dans le SDK et l'OS, points d'arrêt partout |
| `Release` | les paquets `HackerOs.*` de nuget.org, à la version du sous-module | CI, `dotnet publish`, le Hack Store |

Forcez l'un ou l'autre avec `-p:HackerOsUseSource=true` ou `false`.

## Publier dans le Hack Store

1. Remplissez `store/` (icône, descriptions courtes, descriptions, catégorie et étiquettes dans `store.json`).
2. Changez `Version` dans la classe `[AppManifest]` de l'app, validez, puis posez un tag : `git tag v1.0.0 && git push --tags`.
3. Le workflow **Publish to the Hack Store** compile l'app et produit le dossier du catalogue
   `apps/<appId>_<version>/`. Avec un secret `HACK_STORE_TOKEN` (un jeton qui peut créer un fork et
   ouvrir des pull requests), il ouvre la pull request vers `Kazo-ca/HackerOs-AppMarketPlace` depuis
   votre fork ; sans lui, téléchargez l'artefact du workflow et ouvrez la pull request vous-même.

Le catalogue ne prend jamais de binaire de votre part : une fois votre pull request relue et
fusionnée, sa CI compile l'app au commit du tag.

## Publier sur la branche `hackeros-packages`, signé

Le même tag `v*` lance aussi **Publish the HackerOS packages** : il fait le paquet de l'app et le
pousse sur la branche `hackeros-packages` de ce dépôt, en gardant les versions déjà publiées. Les
utilisateurs de HackerOS l'installent alors avec `hackpkg install <owner>/<repo>`, sans le Hack Store.

Signez vos paquets pour que personne d'autre ne puisse publier en votre nom :

1. Une fois, sur votre machine, hors du dépôt :
   `dotnet build src/HackerOs.Template.Solution -t:HackerOsKeyGen -p:HackerOsSigningKeyFile=$HOME/hackeros-signing-key.pem`.
   La clé privée va dans ce nouveau fichier (`*.pem` est ignoré par git) ; la clé publique est affichée.
2. Mettez le contenu du fichier dans le secret `HACKEROS_APP_SIGNING_KEY` de ce dépôt
   (Settings ▸ Secrets and variables ▸ Actions), puis gardez le fichier hors ligne.
3. Publiez la clé publique affichée (dans ce README par exemple). Un administrateur l'ajoute à
   `[signing-keys]` de `/etc/hackeros/app-sources.config`, sous la forme `github.com/<owner>=<clé>`
   (tous vos dépôts) ou `github.com/<owner>/<repo>=<clé>` (celui-ci). Dès lors, un paquet à vous non
   signé ou mal signé est refusé.

Sans le secret, le paquet est publié non signé (SHA-256 seulement) et le run affiche un
avertissement. Pour signer en local, définissez `HACKEROS_APP_SIGNING_KEY` ou passez
`-p:HackerOsSigningKeyFile=<fichier.pem>` à `HackerOsPack`. Pour changer de clé sans coupure, faites
ajouter la nouvelle clé après l'ancienne (séparées par une virgule), changez le secret, puis faites
retirer l'ancienne.
