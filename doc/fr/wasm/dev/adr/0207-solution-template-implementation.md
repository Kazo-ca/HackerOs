> [English version](../../../../en/wasm/dev/adr/0207-solution-template-implementation.md)

# ADR 0207 : modèle `hackeros-solution`, ajustements de mise en œuvre

## Statut

Accepté le 2026-10-30, chantier `todo/1-hackstore-solution-template` (issue
`TODO` #246, chantier 1 du plan du Hack Store). Met en œuvre l'[ADR 0176](0176-app-repository-template.md)
et le §7 de l'[ADR 0188](0188-hack-store-and-app-catalog.md). Ne change aucune de
leurs décisions ; il fixe ce qu'elles laissaient ouvert et remplace une
conséquence de l'ADR 0176 (deux listes des apps de base) par une seule liste.

## Contexte

L'ADR 0176 décide un modèle de solution, un sous-module HackerOS obligatoire,
deux profils (sources en `Debug`, paquets en `Release`) et un troisième mode de
`HackerOs.Apps.targets`. L'ADR 0188 §7 ajoute au modèle un workflow qui soumet
l'app au catalogue sur un tag. Il restait à choisir comment les références de
paquets deviennent des projets, où vit ce mode, comment le sous-module arrive
dans un dossier créé par `dotnet new`, et les détails du workflow.

## Décision

1. **Contenu du modèle.** `dotnet new hackeros-solution -n <Nom>` crée :
   `<Nom>.sln` ; `src/<Nom>` (app fenêtrée) ; `src/<Nom>.Os` (l'hôte, les apps
   de base et l'app, avec `launchSettings.json` pour déboguer dans le
   navigateur) ; `tests/<Nom>.Tests` (xUnit) ; `Directory.Build.props`,
   `Directory.Build.targets`, `global.json`, `.gitignore`, `.gitmodules`
   (`external/HackerOs`, `shallow = true`) ; `README.md` et `readme-fr.md` ;
   `store/` (icône 256×256, descriptions courtes et longues en anglais et en
   français, `store.json`) ; `.github/workflows/ci.yml`,
   `.github/workflows/publish-to-hack-store.yml` et
   `.github/scripts/check-hackeros-submodule.sh`. Paramètres : `--appId`,
   `--publisherId` et `--repository` (`propriétaire/nom`, écrit dans
   `store.json`).
2. **Un fichier `build/HackerOs.ExternalSource.targets` dans le dépôt
   principal.** Le `Directory.Build.targets` du modèle l'importe depuis le
   sous-module quand `HackerOsUseSource` est vrai. Il remplace chaque
   `PackageReference` d'un paquet HackerOS par une `ProjectReference` vers le
   projet du même nom dans le sous-module (la métadonnée `IsSystemApp` suit),
   retire `HackerOs.App.Build` et importe `HackerOs.Apps.targets` à sa place
   pour les projets d'app (`HackerOsAppProject=true`) et d'hôte
   (`HackerOsAppHost=true`). **Pas de liste à tenir** : un paquet HackerOS est
   le projet du même nom, un dossier sous `Shared/`, `Platform/`, `OS/`,
   `Infrastructure/`, `Server/` ou `Tools/`, ou deux dossiers sous `Apps/`. La
   liste des apps de base n'existe donc qu'une fois, en paquets, dans l'hôte.
3. **Frontière du SDK en source externe.** `HackerOs.Apps.targets` reçoit
   `HackerOsExternalSource=true`. La racine HackerOS est celle du sous-module
   (le fichier y est importé), donc `HackerOsSourceRoot` ; la règle `HOSAPP0030`
   vérifie les références qui **entrent** dans le sous-module. Une référence
   vers un autre projet du dépôt de l'app (hors du sous-module) est l'affaire
   de ce dépôt, comme en mode paquet. Une app hors du dépôt principal reçoit
   `IsTrimmable` et l'analyse de trimming, comme en mode paquet.
4. **Les modèles d'app écrivent `HackerOsAppProject=true`.** MSBuild ne peut
   pas décider à l'évaluation qu'un projet est une app parce qu'il référence
   `HackerOs.App.Build`. Les cinq modèles d'app (`window`, `command`,
   `service`, `site` et l'app de la solution) déclarent donc la propriété ;
   elle ne change rien en mode paquet.
5. **Une sortie de restauration par profil.** En `Debug` (sources),
   `BaseIntermediateOutputPath` vaut `obj/source/` ; en `Release`, `obj/`.
   Changer de configuration dans Visual Studio ne lit jamais les références de
   l'autre profil. `obj/**` est exclu des éléments par défaut.
6. **Le sous-module.** `dotnet new` ne sait pas créer un lien de sous-module
   git : le `README.md` généré donne `git submodule add --depth 1 … external/HackerOs`
   puis `git -C external/HackerOs checkout v<version>`. Le dépôt template
   GitHub, lui, contient le lien. Sans sous-module, le build s'arrête sur
   **`HOSAPP0070`** avec la commande `git submodule update --init`.
   `HackerOsSourceRoot` (par défaut `external/HackerOs/wasm2/HackerOs/`) peut
   pointer ailleurs ; le test du point 9 le pointe vers le dépôt lui-même.
7. **CI du modèle.** `check-hackeros-submodule.sh` refuse un sous-module
   modifié ou qui n'est pas exactement sur le tag `v<version>` de son
   `Versions.props` ; puis `dotnet build -c Release` et `dotnet test`.
8. **Workflow de publication (ADR 0188 §7).** Sur un tag `v*` (ou à la main) :
   même vérification du sous-module, `dotnet build -c Release -t:HackerOsPack`
   de l'app, refus si le tag n'est pas la `Version` du manifeste généré, puis
   dossier `apps/<appId>_<version>/` : `icon.png`, `short_description.*.txt`,
   `description.*.md`, `screenshots/` si présent, le `manifest.json` du build
   Release et `store.json` complété par le commit du tag. Le dossier est
   toujours laissé en artefact du workflow. Avec le secret
   **`HACK_STORE_TOKEN`** de l'auteur, le workflow crée son fork de
   `Kazo-ca/HackerOs-AppMarketPlace`, y pousse une branche `<appId>_<version>`
   et ouvre la pull request. L'app du modèle déclare sa `Version` dans sa
   classe `[AppManifest]` : on la change, puis on pose le tag.
9. **Test.** `HackerOs.Templates.E2E.Tests` génère une solution, vérifie
   `HOSAPP0070` sans sous-module, puis, `HackerOsSourceRoot` pointé vers le
   dépôt : build et tests en `Debug` (toutes les références HackerOS sont des
   projets, l'hôte agrège l'app et les apps système), build et tests en
   `Release` (toutes sont des paquets du flux local), et `HackerOsPack` comme le
   workflow.
10. **Visual Studio et Rider.** Les six modèles ont un
    `.template.config/ide.host.json` (ordre, libellés des paramètres) et une
    icône 32×32.
11. **Le dépôt template `Kazo-ca/HackerOs-AppTemplate`.** Il reçoit la sortie
    de `dotnet new hackeros-solution` (app `HackerOs.App`, `--appId com.example.my-app`)
    et le lien du sous-module. Le mode source externe et `HackerOsPack`
    n'existent que dans une version publiée **après** ce chantier : tant
    qu'aucun tag de release ne les contient, le sous-module du dépôt template
    pointe sur la pointe de `main` qui les contient et sa CI échoue sur la
    vérification du tag. La PR du dépôt template reste en brouillon jusqu'à
    cette release ; le marquage « Template repository » est fait par Benoit
    dans les réglages GitHub.

## Conséquences

- Le dépôt principal porte un fichier de build de plus,
  `HackerOs.ExternalSource.targets`, couvert par le test du point 9.
- Renommer un projet publié ou le déplacer hors des dossiers listés au point 2
  casse le profil `Debug` des dépôts d'apps : le test du point 9 le détecte
  pour les paquets que le modèle référence.
- Le profil `Debug` compile tout l'OS (premier build de l'ordre de deux
  minutes) ; les builds suivants sont incrémentaux.
- Un dépôt d'app créé avec une version qui précède ce chantier n'a pas le
  profil `Debug` : il le reçoit en régénérant ses fichiers de build depuis le
  modèle.

## Tâches

- [x] `build/HackerOs.ExternalSource.targets` et le mode source externe de `HackerOs.Apps.targets`.
- [x] Modèle `hackeros-solution` (sous-module, profils, CI, workflow de publication, `store/`).
- [x] `ide.host.json` et icône des six modèles ; `HackerOsAppProject=true` dans les modèles d'app.
- [x] Test des deux profils dans `HackerOs.Templates.E2E.Tests`.
- [x] `building-on-hackeros.md` corrigé (paquets sur nuget.org), [`app-repository.md`](../../framework/app-repository.md).
- [ ] Dépôt template : PR en brouillon, finalisée après la release qui contient ce chantier ; « Template repository » coché par Benoit.
