> [English version](../../../en/wasm/framework/app-repository.md)

# Dépôt d'app : le modèle `hackeros-solution`

## Objet

Le point de départ d'une app HackerOS dans son propre dépôt : une solution qui
se lance en débogage dans l'OS, dont la version de HackerOS est fixée par un
sous-module, et qui se publie dans le Hack Store par un tag. Décisions :
[ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0188](../dev/adr/0188-hack-store-and-app-catalog.md) §7,
[ADR 0207](../dev/adr/0207-solution-template-implementation.md).
Le guide général d'un dépôt qui utilise HackerOS est
[`building-on-hackeros.md`](building-on-hackeros.md).

## Créer le dépôt

Deux façons, même résultat :

- **Depuis GitHub** : « Use this template » sur
  [`Kazo-ca/HackerOs-AppTemplate`](https://github.com/Kazo-ca/HackerOs-AppTemplate),
  puis `git clone --recurse-submodules`. Le sous-module est déjà là.
- **Depuis la ligne de commande ou Visual Studio** :

  ```bash
  dotnet new install HackerOs.Templates
  dotnet new hackeros-solution -n Contoso.Notes --appId com.contoso.notes \
      --publisherId pub.contoso --repository contoso/HackerOs-App-Notes
  cd Contoso.Notes
  git init
  git submodule add --depth 1 https://github.com/Kazo-ca/HackerOs.git external/HackerOs
  git -C external/HackerOs checkout v<version de HackerOS>
  ```

  `dotnet new` ne sait pas créer le lien du sous-module ; sans lui, le build
  s'arrête sur `HOSAPP0070` avec la commande à lancer. Dans Visual Studio
  (Nouveau projet) ou Rider, le modèle s'appelle « HackerOS app solution ».

## Ce qui est généré

| Chemin | Contenu |
| --- | --- |
| `<Nom>.sln` | la solution (dossiers `src` et `tests`) |
| `src/<Nom>/` | l'app fenêtrée ; sa classe `[AppManifest]` (avec `Version`) génère `manifest.json` |
| `src/<Nom>.Os/` | l'hôte : tout HackerOS, les apps de base et l'app ; le projet à démarrer |
| `tests/<Nom>.Tests/` | tests xUnit de l'app |
| `Directory.Build.props` | racine du sous-module, profil, import de `Versions.props` |
| `Directory.Build.targets` | import du mode source externe, contrôle du sous-module |
| `.gitmodules` | `external/HackerOs`, clone superficiel |
| `store/` | `icon.png` (256×256), `short_description.{en,fr}.txt`, `description.{en,fr}.md`, `store.json`, `screenshots/` facultatif |
| `.github/workflows/ci.yml` | CI : sous-module vérifié, build et tests en `Release` |
| `.github/workflows/publish-to-hack-store.yml` | soumission au catalogue sur un tag `v*` |
| `.github/workflows/publish-packages.yml` | paquet signé poussé sur la branche `hackeros-packages` sur un tag `v*` ([ADR 0219](../dev/adr/0219-app-authors-signing-tool.md)) |
| `README.md`, `readme-fr.md` | ces étapes, pour le dépôt créé |

## La version de HackerOS

`external/HackerOs/wasm2/HackerOs/build/Versions.props` est la version du
dépôt : `Directory.Build.props` l'importe, et chaque référence `HackerOs.*`
utilise `$(HackerOsVersion)`. Changer de version, c'est placer le sous-module
sur un autre tag de release et valider ce changement. On ne modifie jamais le
sous-module : la CI refuse un sous-module modifié ou hors de son tag
(`.github/scripts/check-hackeros-submodule.sh`).

## Les deux profils

| Profil | HackerOS vient de | Sortie de restauration |
| --- | --- | --- |
| `Debug` (`HackerOsUseSource=true`) | les projets du sous-module : on entre dans le SDK et l'OS, points d'arrêt partout | `obj/source/` |
| `Release` (`HackerOsUseSource=false`) | les paquets `HackerOs.*` de nuget.org, à la version du sous-module | `obj/` |

`-p:HackerOsUseSource=true` ou `false` force l'un ou l'autre ;
`-p:HackerOsSourceRoot=<dossier>` pointe vers une autre copie de HackerOS
(dossier de `HackerOs.sln`). Le premier build `Debug` compile tout l'OS.

### Comment marche le profil `Debug`

`build/HackerOs.ExternalSource.targets` (dans le dépôt HackerOS) est importé
depuis le sous-module. Il :

1. trouve les projets qui peuvent être des paquets : `Shared/*`, `Platform/*`,
   `OS/*`, `Infrastructure/*`, `Server/*`, `Tools/*` et `Apps/*/*` (un paquet
   = le projet du même nom) ;
2. remplace chaque `PackageReference` vers l'un d'eux par une
   `ProjectReference`, en gardant `IsSystemApp` pour les apps de base de
   l'hôte ;
3. retire `HackerOs.App.Build` et importe `HackerOs.Apps.targets` pour les
   projets qui déclarent `HackerOsAppProject` ou `HackerOsAppHost` : même
   générateur de manifeste, mêmes analyseurs, même agrégation dans l'hôte que
   dans le dépôt HackerOS (`HackerOsExternalSource=true`).

La frontière du SDK (`HOSAPP0030`) vérifie les références qui entrent dans le
sous-module ; un projet de votre dépôt peut en référencer un autre. Une app
ajoutée avec `dotnet new hackeros-window` (ou `-command`, `-service`, `-site`)
déclare déjà `HackerOsAppProject=true` ; ajoutez-la à l'hôte par
`dotnet add src/<Nom>.Os reference …`.

## Publier dans le Hack Store

1. Remplir `store/` : icône PNG 256×256 (200 Ko au plus), description courte
   anglaise de 120 caractères au plus (le français est facultatif),
   descriptions, et dans `store.json` la catégorie (liste fermée du
   catalogue), les étiquettes et la licence. `repository` vient de
   `--repository`.
2. Changer `Version` dans la classe `[AppManifest]`, valider, poser le tag
   `v<version>` et le pousser.
3. Le workflow **Publish to the Hack Store** vérifie le sous-module, compile
   l'app en `Release`, la met en paquet (`HackerOsPack`), refuse un tag qui
   n'est pas la version du manifeste et produit `apps/<appId>_<version>/`
   (contenu de `store/`, `manifest.json` généré, `store.json` avec le commit du
   tag), laissé en artefact du workflow.
4. Avec le secret `HACK_STORE_TOKEN` (un jeton de l'auteur qui peut créer un
   fork et ouvrir une pull request), le workflow ouvre la pull request vers
   `Kazo-ca/HackerOs-AppMarketPlace` depuis votre fork. Sans lui, téléchargez
   l'artefact et ouvrez la pull request vous-même.

Le catalogue ne prend jamais de binaire : après relecture et fusion, sa CI
compile l'app au commit indiqué (ADR 0188 §4 à §6).

## Publier sur `hackeros-packages` et signer

[ADR 0219](../dev/adr/0219-app-authors-signing-tool.md). Le même tag `v*` lance
**Publish the HackerOS packages** (`publish-packages.yml`, seul workflow avec
`contents: write`) : il récupère la branche `hackeros-packages` (orpheline la
première fois), y fait le paquet (`HackerOsPack`, `HackerOsPackageOutput`), garde
les versions déjà publiées et leurs signatures, puis pousse la branche.
`hackpkg install <owner>/<repo>` installe alors l'app sans le Store.

Pour signer :

1. Une fois, hors du dépôt :
   `dotnet build src/<Nom> -t:HackerOsKeyGen -p:HackerOsSigningKeyFile=$HOME/hackeros-signing-key.pem`.
   La clé privée (PKCS#8, droits `0600`) va dans ce nouveau fichier, jamais
   remplacé ; la clé publique et la ligne `github.com/<owner>=…` sont affichées.
   `*.pem` est dans le `.gitignore` du modèle.
2. Mettre le contenu du fichier dans le secret `HACKEROS_APP_SIGNING_KEY` du
   dépôt, puis garder le fichier hors ligne.
3. Publier la clé publique ; un administrateur l'ajoute à `[signing-keys]` de
   `/etc/hackeros/app-sources.config`. Dès lors, un paquet non signé ou mal
   signé de ce dépôt est refusé.

Sans le secret, le paquet part non signé (SHA-256 seulement) avec un
avertissement. En local : variable `HACKEROS_APP_SIGNING_KEY` ou
`-p:HackerOsSigningKeyFile=<fichier.pem>` sur `HackerOsPack`. Il faut une
version de HackerOS qui contient l'ADR 0219 (la suivante après 2.2.0).

## Le dépôt template GitHub

[`Kazo-ca/HackerOs-AppTemplate`](https://github.com/Kazo-ca/HackerOs-AppTemplate)
est la sortie de `dotnet new hackeros-solution -n HackerOs.App` plus le lien du
sous-module. On le régénère depuis le modèle, jamais à la main. Il ne peut
fonctionner qu'avec un sous-module sur une release qui contient ce modèle
(2.2.0 ou plus récente, ADR 0207 §11).

## Tâches

- [x] Mode source externe (`HackerOs.ExternalSource.targets`, `HackerOs.Apps.targets`).
- [x] Modèle `hackeros-solution`, CI et workflow de publication.
- [x] `ide.host.json` des modèles.
- [x] Test des deux profils (`HackerOs.Templates.E2E.Tests`).
- [ ] Dépôt template régénéré sur la release qui contient ce modèle.
- [x] Workflow `publish-packages.yml` et signature des paquets (ADR 0219).
