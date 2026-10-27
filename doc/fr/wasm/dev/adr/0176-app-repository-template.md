> [English version](../../../../en/wasm/dev/adr/0176-app-repository-template.md)

# ADR 0176 : dépôt template d'apps, sous-module en débogage et paquets en production

## Statut

Accepté le 2026-10-27 par Benoit, après l'évaluation demandée le même jour
(« un projet et un git-module qui pointe vers la repository principal pour aider
au debug et qu'il utilise les packages nuget pour la production »). Ses réponses :
dépôt template avec sous-module obligatoire ; on ne modifie pas l'OS pendant le
développement d'une app, le sous-module sert à contrôler le déploiement et à
lancer l'app en débogage ; paquets en production et source du sous-module en
débogage (« les deux selon le profil ») ; Visual Studio par les modèles
`dotnet new`, sans extension VSIX. Complète l'[ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
(paquets, modèles, mode paquet) et l'[ADR 0173](0173-automatic-releases-from-the-version-file.md)
(publication sur nuget.org). Aucun code n'est écrit par cette ADR.

## Contexte

- `HackerOs.Templates` contient cinq modèles de **projet** (`hackeros-window`,
  `-command`, `-service`, `-site`, `-host`). Ils partent sur nuget.org à chaque
  release, avec les 41 autres paquets, leurs symboles (`.snupkg`) et Source Link.
- Un dépôt tiers suit [`building-on-hackeros.md`](../../framework/building-on-hackeros.md) :
  il assemble lui-même sa solution. Ce guide dit encore que les paquets ne sont
  jamais publiés sur nuget.org, ce qui est faux depuis la 2.1.0.
- `build/HackerOs.Apps.targets` a deux modes : le dépôt (références de projets,
  générateur de manifestes compilé dans la solution) et le mode paquet
  (`HackerOsBuildFromPackage=true`, tout vient de `HackerOs.App.Build`).
- Benoit veut un point de départ prêt à l'emploi : une solution qui se lance en
  débogage dans l'OS, et dont le déploiement est contrôlé par la version de l'OS
  qu'elle épingle.

## Décision

1. **La source du template reste dans le dépôt principal.** Un nouveau modèle de
   solution `hackeros-solution` (type `solution`) dans `HackerOs.Templates` génère :
   `.sln`, un hôte (`src/<Nom>.Os`, celui de `hackeros-host`), une app fenêtrée,
   un projet de tests, `Directory.Build.props`, `global.json`, `.gitignore`,
   `.gitmodules`, un workflow CI et un `README.md`. Il est publié avec les autres
   modèles, à la version de la release.
2. **Dépôt template GitHub.** Un dépôt de l'organisation Kazo-ca, marqué
   « Template repository », contient la sortie de `dotnet new hackeros-solution`.
   Il est régénéré depuis le modèle, jamais modifié à la main ailleurs que dans
   le modèle.
3. **Sous-module obligatoire.** `external/HackerOs` pointe vers le dépôt
   principal, sur un tag de release `v<version>`, en clone superficiel
   (`shallow = true` dans `.gitmodules`). Son `build/Versions.props` est **la**
   version du dépôt d'apps : `Directory.Build.props` l'importe au lieu d'écrire
   `HackerOsVersion`. Changer de version de l'OS, c'est déplacer le sous-module
   sur un autre tag. Si le sous-module manque, le build échoue avec un message
   qui donne `git submodule update --init`.
4. **Deux profils de build.**
   - **Production** (`Release`, CI, `dotnet publish`) : `PackageReference` des
     paquets `HackerOs.*` de nuget.org, à la version du sous-module. C'est le
     mode paquet actuel, sans changement.
   - **Débogage** (`Debug`) : les mêmes références deviennent des
     `ProjectReference` vers les projets du sous-module (SDK, `HackerOs.Ecosystem.Host`,
     apps de base de l'hôte, générateur de manifestes). On peut entrer dans le
     code de l'OS et y poser des points d'arrêt.
   - La propriété `HackerOsUseSource` choisit le profil (vraie par défaut en
     `Debug`, fausse sinon) ; on peut la forcer dans les deux sens.
5. **Troisième mode de `HackerOs.Apps.targets` : la source externe.** Activé par
   `HackerOsUseSource=true` hors du dépôt principal. Le générateur vient du projet
   `Tools` du sous-module ; la règle de frontière sur les `ProjectReference`
   (`HOSAPP0030`) se calcule à partir de `HackerOsSourceRoot` (par défaut
   `external/HackerOs/wasm2/HackerOs`) au lieu des chemins du dépôt. Les
   inspections et les codes `HOSAPP*` restent identiques.
6. **L'OS n'est pas modifié depuis un dépôt d'apps.** Le sous-module est en
   lecture seule par convention. La CI du template vérifie que le sous-module est
   propre et exactement sur le tag `v$(HackerOsVersion)`, puis construit en
   `Release` : une modification locale de l'OS ne peut pas partir en production.
7. **Visual Studio.** Chaque modèle reçoit un `.template.config/ide.host.json`
   (icône, libellés des paramètres `appId` et `publisherId`). Visual Studio et
   Rider listent les modèles installés par `dotnet new install HackerOs.Templates`.
   Pas d'extension VSIX.
8. **Test dans le dépôt principal.** `HackerOs.Templates.E2E.Tests` génère une
   solution avec `hackeros-solution`, pointe `HackerOsSourceRoot` vers le dépôt
   lui-même, et construit les deux profils : `Debug` (source externe) et
   `Release` (flux local des paquets).

## Conséquences

- Le dépôt principal reste la seule source des modèles ; le dépôt template n'est
  qu'une copie générée.
- Un troisième mode de build est à maintenir dans `HackerOs.Apps.targets` ; le
  test du point 8 le protège.
- `Debug` et `Release` ne compilent pas l'OS de la même façon. Comme les deux
  viennent du même tag, le comportement attendu est identique ; la CI teste
  `Release`, ce qui part réellement.
- Cloner le dépôt d'apps demande `--recurse-submodules` ; le clone superficiel
  limite le poids du sous-module.
- La liste des apps de base de l'hôte existe en deux formes (paquets et projets) ;
  le modèle d'hôte les garde côte à côte et le test du point 8 vérifie qu'elles
  concordent.

## Tâches

- [ ] Corriger `building-on-hackeros.md` (fr et en) : paquets et modèles sur nuget.org.
- [ ] `ide.host.json` pour les cinq modèles existants.
- [ ] Mode source externe dans `HackerOs.Apps.targets` (`HackerOsUseSource`, `HackerOsSourceRoot`).
- [ ] Modèle `hackeros-solution` (sous-module, import de `Versions.props`, profils, CI).
- [ ] Test des deux profils dans `HackerOs.Templates.E2E.Tests`.
- [ ] Créer le dépôt template depuis le modèle (Benoit crée le dépôt dans Kazo-ca).
