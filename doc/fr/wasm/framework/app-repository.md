> [English version](../../../en/wasm/framework/app-repository.md)

# Dépôt d'app : le modèle `kazoos-solution`

## Objet

Le point de départ d'une app Kazo OS dans son propre dépôt : une solution qui
se lance en débogage dans l'OS, dont la version de Kazo OS est fixée par un
sous-module, et qui se publie dans le Kazo OS Store par un tag. Décisions :
[ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0188](../dev/adr/0188-hack-store-and-app-catalog.md) §7,
[ADR 0207](../dev/adr/0207-solution-template-implementation.md).
Le guide général d'un dépôt qui utilise Kazo OS est
[`building-on-kazoos.md`](building-on-kazoos.md).

## Créer le dépôt

Deux façons, même résultat :

- **Depuis GitHub** : « Use this template » sur
  [`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate),
  puis `git clone --recurse-submodules`. Le sous-module est déjà là.
- **Depuis la ligne de commande ou Visual Studio** :

  ```bash
  dotnet new install Kazo.Os.Templates
  dotnet new kazoos-solution -n Contoso.Notes --appId com.contoso.notes \
      --publisherId pub.contoso --repository contoso/Os-App-Notes
  cd Contoso.Notes
  git init
  git submodule add --depth 1 https://github.com/Kazo-ca/Os.git external/KazoOs
  git -C external/KazoOs checkout v<version de Kazo OS>
  ```

  `dotnet new` ne sait pas créer le lien du sous-module ; sans lui, le build
  s'arrête sur `KOSAPP0070` avec la commande à lancer. Dans Visual Studio
  (Nouveau projet) ou Rider, le modèle s'appelle « Kazo OS app solution ».

## Ce qui est généré

| Chemin | Contenu |
| --- | --- |
| `<Nom>.sln` | la solution (dossiers `src` et `tests`) |
| `src/<Nom>/` | l'app fenêtrée ; sa classe `[AppManifest]` (avec `Version`) génère `manifest.json` |
| `src/<Nom>.Os/` | l'hôte : tout Kazo OS, les apps de base et l'app ; le projet à démarrer |
| `tests/<Nom>.Tests/` | tests xUnit de l'app |
| `Directory.Build.props` | racine du sous-module, profil, import de `Versions.props` |
| `Directory.Build.targets` | import du mode source externe, contrôle du sous-module |
| `.gitmodules` | `external/KazoOs`, clone superficiel |
| `store/` | `icon.png` (256×256), `short_description.{en,fr}.txt`, `description.{en,fr}.md`, `store.json`, `screenshots/` facultatif |
| `.github/workflows/ci.yml` | CI : sous-module vérifié, build et tests en `Release` |
| `.github/workflows/publish-to-kazoos-store.yml` | soumission au catalogue sur un tag `v*` |
| `.github/workflows/tag-release.yml` | pose sur un commit de `main` le tag de la version du manifeste et lance les deux workflows ci-dessus (`workflow_dispatch`) |
| `.github/workflows/publish-packages.yml` | paquet non signé poussé sur la branche `kazoos-packages` sur un tag `v*` ([ADR 0233](../dev/adr/0233-sign-apps-only-in-the-marketplace.md)) |
| `README.md`, `readme-fr.md` | ces étapes, pour le dépôt créé |

## La version de Kazo OS

`external/KazoOs/wasm2/KazoOs/build/Versions.props` est la version du
dépôt : `Directory.Build.props` l'importe, et chaque référence `Kazo.Os.*`
utilise `$(KazoOsVersion)`. Changer de version, c'est placer le sous-module
sur un autre tag de release et valider ce changement. On ne modifie jamais le
sous-module : la CI refuse un sous-module modifié ou hors de son tag
(`.github/scripts/check-kazoos-submodule.sh`).

## Les deux profils

| Profil | Kazo OS vient de | Sortie de restauration |
| --- | --- | --- |
| `Debug` (`KazoOsUseSource=true`) | les projets du sous-module : on entre dans le SDK et l'OS, points d'arrêt partout | `obj/source/` |
| `Release` (`KazoOsUseSource=false`) | les paquets `Kazo.Os.*` de nuget.org, à la version du sous-module | `obj/` |

`-p:KazoOsUseSource=true` ou `false` force l'un ou l'autre ;
`-p:KazoOsSourceRoot=<dossier>` pointe vers une autre copie de Kazo OS
(dossier de `Kazo.Os.sln`). Le premier build `Debug` compile tout l'OS.

### Comment marche le profil `Debug`

`build/Kazo.Os.ExternalSource.targets` (dans le dépôt Kazo OS) est importé
depuis le sous-module. Il :

1. trouve les projets qui peuvent être des paquets : `Shared/*`, `Platform/*`,
   `OS/*`, `Infrastructure/*`, `Server/*`, `Tools/*` et `Apps/*/*` (un paquet
   = le projet du même nom) ;
2. remplace chaque `PackageReference` vers l'un d'eux par une
   `ProjectReference`, en gardant `IsSystemApp` pour les apps de base de
   l'hôte ;
3. retire `Kazo.Os.App.Build` et importe `Kazo.Os.Apps.targets` pour les
   projets qui déclarent `KazoOsAppProject` ou `KazoOsAppHost` : même
   générateur de manifeste, mêmes analyseurs, même agrégation dans l'hôte que
   dans le dépôt Kazo OS (`KazoOsExternalSource=true`).

La frontière du SDK (`KOSAPP0030`) vérifie les références qui entrent dans le
sous-module ; un projet de votre dépôt peut en référencer un autre. Une app
ajoutée avec `dotnet new kazoos-window` (ou `-command`, `-service`, `-site`)
déclare déjà `KazoOsAppProject=true` ; ajoutez-la à l'hôte par
`dotnet add src/<Nom>.Os reference …`.

## Publier dans le Kazo OS Store

1. Remplir `store/` : icône PNG 256×256 (200 Ko au plus), description courte
   anglaise de 120 caractères au plus (le français est facultatif),
   descriptions, et dans `store.json` la catégorie (liste fermée du
   catalogue), les étiquettes et la licence. `repository` vient de
   `--repository` ; `project` nomme le `.csproj` de l'app, que le catalogue
   compile (à la racine, `KazoOsPack` tournerait aussi sur le projet de tests,
   qui n'a pas cette cible).
2. Changer `Version` dans la classe `[AppManifest]`, valider dans `main`, puis
   poser le tag : lancer le workflow **Tag a release** (`tag-release.yml`, depuis
   l'onglet Actions ou, pour un agent, via l'API GitHub), ou poser le tag
   `v<version>` et le pousser soi-même. Le workflow pose sur un commit de `main`
   (la tête par défaut) le tag de la version du manifeste et y lance les deux
   workflows de publication, car un tag poussé avec le jeton du workflow ne les
   lance pas ; avec `publish` désactivé, il pose seulement le tag (version déjà
   publiée).
3. Le workflow **Publish to the Kazo OS Store** vérifie le sous-module, compile
   l'app en `Release`, la met en paquet (`KazoOsPack`), refuse un tag qui
   n'est pas la version du manifeste et produit `apps/<appId>_<version>/`
   (contenu de `store/`, `manifest.json` **du paquet**, `store.json` avec le
   commit du tag), laissé en artefact du workflow. Le manifeste du paquet est le
   manifeste généré plus toutes les ressources statiques que `KazoOsPack` y a
   ajoutées ([ADR 0221](../dev/adr/0221-pack-all-static-web-assets-of-an-app.md)) :
   c'est celui que le catalogue compare au paquet que sa CI construit. Le paquet
   contient aussi les assemblages satellites de l'app (ses `.resx` par culture,
   [ADR 0222](../dev/adr/0222-installed-app-satellite-assemblies.md)).
4. Avec le secret `HACK_STORE_TOKEN` (un jeton de l'auteur qui peut créer un
   fork et ouvrir une pull request), le workflow ouvre la pull request vers
   `Kazo-ca/Os-AppMarketPlace` depuis votre fork. Sans lui, téléchargez
   l'artefact et ouvrez la pull request vous-même.

Le catalogue ne prend jamais de binaire : après relecture et fusion, sa CI
compile l'app au commit indiqué (ADR 0188 §4 à §6).

### Le secret `HACK_STORE_TOKEN`

C'est un jeton d'accès personnel GitHub de **l'auteur de l'app** : ni Kazo OS
ni le catalogue ne le fournissent. Avec lui, le workflow lit le nom de
l'auteur, forke `Kazo-ca/Os-AppMarketPlace` sur son compte, y pousse la
branche `<appId>_<version>` et ouvre la pull request vers le catalogue.

Créer le jeton (un jeton « classic ») :

1. GitHub ▸ photo du profil ▸ **Settings** ▸ **Developer settings** ▸
   **Personal access tokens** ▸ **Tokens (classic)** ▸ **Generate new token
   (classic)**.
2. *Note* : par exemple `<dépôt> → Kazo OS Store` ; *Expiration* : courte
   (90 jours par exemple), à renouveler.
3. Scopes : **`repo`** (forker, pousser, ouvrir la pull request) et
   **`workflow`** (GitHub refuse un push qui apporte au fork des changements de
   `.github/workflows` du catalogue que le fork n'a pas encore).
4. **Generate token**, puis copier le jeton : il ne s'affiche qu'une fois.

L'ajouter au dépôt de l'app : **Settings** ▸ **Secrets and variables** ▸
**Actions** ▸ **New repository secret**, nom `HACK_STORE_TOKEN`. Ou en ligne de
commande :

```bash
gh secret set HACK_STORE_TOKEN --repo <propriétaire>/<dépôt>
```

- **Pourquoi « classic »** : un jeton à grain fin ne vaut que pour un seul
  propriétaire (le compte de l'auteur **ou** une organisation), alors que le
  workflow touche le fork de l'auteur et le dépôt du catalogue. Si
  l'organisation du dépôt de l'app interdit les jetons classic, l'autoriser
  dans ses réglages (*Personal access tokens*).
- **Prudence** : `repo` donne accès à tous les dépôts de l'auteur. Garder une
  expiration courte, ne mettre le jeton que dans ce secret, le révoquer au
  moindre doute.
- **Sans le secret**, rien ne bloque : le dossier `apps/<appId>_<version>/`
  reste en artefact du workflow, et l'auteur ouvre la pull request lui-même.
- Ce jeton ne signe rien : les apps sont signées seulement dans le dépôt du
  marketplace ([ADR 0233](../dev/adr/0233-sign-apps-only-in-the-marketplace.md)).

## Publier sur `kazoos-packages`

[ADR 0219](../dev/adr/0219-app-authors-signing-tool.md) §3, sans signature
depuis l'[ADR 0233](../dev/adr/0233-sign-apps-only-in-the-marketplace.md). Le
même tag `v*` lance **Publish the Kazo OS packages** (`publish-packages.yml`,
seul workflow avec `contents: write`) : il récupère la branche
`kazoos-packages` (orpheline la première fois), y fait le paquet
(`KazoOsPack`, `KazoOsPackageOutput`), garde les versions déjà publiées, puis
pousse la branche. `kazopkg install <owner>/<repo>` installe alors l'app sans
le Store.

Ces paquets ne sont **pas signés** : aucun dépôt d'app ne signe, seul
`Kazo-ca/Os-AppMarketPlace` signe les paquets du Kazo OS Store. L'OS les
vérifie par leur SHA-256 et demande un administrateur pour une source non
fiable (ADR 0208, ADR 0214 §2.4). Un dépôt d'app qui a encore un secret
`HACKEROS_APP_SIGNING_KEY` (ADR 0219) peut le supprimer : plus rien ne le lit.

## Le dépôt template GitHub

[`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate)
est la sortie de `dotnet new kazoos-solution -n Kazo.Os.App` plus le lien du
sous-module. On le régénère depuis le modèle, jamais à la main. Il ne peut
fonctionner qu'avec un sous-module sur une release qui contient ce modèle
(2.2.0 ou plus récente, ADR 0207 §11).

## Tâches

- [x] Mode source externe (`Kazo.Os.ExternalSource.targets`, `Kazo.Os.Apps.targets`).
- [x] Modèle `kazoos-solution`, CI et workflow de publication.
- [x] `ide.host.json` des modèles.
- [x] Test des deux profils (`Kazo.Os.Templates.E2E.Tests`).
- [ ] Dépôt template régénéré sur la release qui contient ce modèle.
- [x] Workflow `publish-packages.yml` et signature des paquets (ADR 0219).
- [x] Signature retirée des dépôts d'apps et du modèle (ADR 0233).
