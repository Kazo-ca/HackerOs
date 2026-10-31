> [English version](../../../../en/wasm/dev/adr/0220-pack-all-static-web-assets-of-an-app.md)

# ADR 0220 : mettre en paquet toutes les ressources statiques d'une app et charger son CSS isolé

## Statut

Accepté le 2026-10-31 (Benoit a choisi de corriger HackerOS plutôt que de
demander à chaque app de tout déclarer).

- **Complète :** [ADR 0206](0206-serve-installed-app-assets.md) (ressources
  `wwwroot/` des apps installées) et [ADR 0182](0182-install-apps-from-github.md)
  §1 (`HackerOsPack`).
- **Ne change pas :** [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
  §5 : chaque fichier servi reste déclaré dans le manifeste avec son SHA-256, et
  vérifié à la première lecture.

## Contexte

- Une app compilée dans l'OS reçoit de Blazor toutes ses ressources statiques à
  `_content/<assemblage>/` : son `wwwroot/`, le JavaScript collocalisé d'un
  composant (`Composant.razor.js`, AGENTS.md II.2) et le bundle de son CSS
  isolé (`<assemblage>.bundle.scp.css`), que la feuille
  `HackerOs.Ecosystem.styles.css` de l'hôte importe.
- Une app installée par `hackpkg` ou le Hack Store reçoit seulement les fichiers
  de son paquet. `HackerOsPack` n'y met que les `Assets` déclarés par
  `AppDefinition.Assets` (souvent les icônes), et l'ADR 0206 ne sert que ceux-là.
  Le reste de `wwwroot/` manque, comme le JavaScript collocalisé, qui n'est pas
  dans `wwwroot/` et ne peut pas être déclaré.
- Le CSS isolé d'une app installée n'est jamais chargé : aucune feuille de l'OS
  ne l'importe. Une app écrite selon les règles du projet (styles dans
  `.razor.css`) s'affiche sans ses styles une fois installée.
- La compilation d'un projet Razor écrit déjà la liste exacte de ces ressources
  dans `obj/<configuration>/<tfm>/staticwebassets.build.json` (chemin servi,
  fichier source, origine).

## Décision

### 1. `HackerOsPack` met en paquet toutes les ressources statiques du projet

- La cible passe à l'outil `pack` le manifeste des ressources statiques de la
  compilation (`$(StaticWebAssetBuildManifestPath)`, sinon
  `$(IntermediateOutputPath)staticwebassets.build.json`), par `--web-assets`.
- L'outil garde les ressources **du projet lui-même** (origine `Discovered` ou
  `Computed`, rôle `Primary`, pas réservées à la publication) : le `wwwroot/`, le
  JavaScript collocalisé et le bundle `<assemblage>.bundle.scp.css`. Il laisse
  les ressources des paquets et projets référencés (le SDK vient avec l'OS) et
  le bundle d'application `<assemblage>.styles.css` (celui d'un hôte).
- Le chemin servi est le `RelativePath` de la compilation sans son motif
  d'empreinte (`#[.{fingerprint}]?`, `#[.{fingerprint}]!`) :
  `Components/Board.razor.js`, `Contoso.Notes.bundle.scp.css`.
- Chaque ressource qui n'est pas déjà dans `Assets` y est ajoutée, avec son
  SHA-256 et un type déduit de l'extension (`.css` → `css`, `.js`/`.mjs` →
  `javaScriptModule`, image → `image`, sinon `data`). Une ressource déjà déclarée
  garde sa déclaration (rôle d'icône compris). Un chemin que le manifeste
  refuserait (`..`, `#`, `?`) est laissé de côté avec un avertissement.
- Seul le manifeste **du paquet** change. Le `manifest.json` généré dans le
  projet et le manifeste des apps compilées dans l'OS restent tels quels : une app
  compilée reçoit déjà tout de Blazor.

### 2. L'OS charge le CSS isolé d'une app installée

- Quand `InstalledAppAssetPublisher` a copié dans le cache (ADR 0206 §3) le
  fichier `<assemblage>.bundle.scp.css` de l'app (assemblage du point d'entrée),
  il ajoute une fois par session `<link rel="stylesheet">` vers
  `_content/<assemblage>/<assemblage>.bundle.scp.css` à la fin du `<head>` de la
  page, par `installedAppAssets.js`.
- Sans service worker (ADR 0206 §5), rien n'est copié et aucun lien n'est
  ajouté : la feuille ne serait pas trouvée.
- La feuille reste chargée jusqu'au rechargement de la page, comme le CSS d'une
  app compilée ; ses sélecteurs portent l'attribut d'isolation propre à
  l'assemblage, ils ne touchent pas les autres apps.

### 3. Le dossier du catalogue reçoit le manifeste du paquet

Le catalogue compare le `manifest.json` du dossier `apps/<appId>_<version>/` à
celui du paquet que sa CI construit (`PackageCheck`, ADR 0188 §5 étape 4). Le
workflow `publish-to-hack-store.yml` du modèle `hackeros-solution` copie donc le
`manifest.json` **du paquet** (extrait du `.hackapp`), et non plus celui du
projet.

## Conséquences

- Une app installée s'affiche et se comporte comme compilée dans l'OS, sans rien
  déclarer de plus : ses modules, son JavaScript collocalisé, ses Web Workers et
  ses styles sont là.
- Le paquet grossit de tout le `wwwroot/` du projet : un fichier qui ne doit pas
  être livré n'a rien à faire dans `wwwroot/` (même règle que pour une app
  compilée, que Blazor publie en entier). Les limites du paquet (2 000 fichiers,
  64 Mio compressés, 128 Mio décompressés, `AppPackage`) s'appliquent désormais à
  tout le `wwwroot/`, et la copie dans le cache au premier lancement de la
  session porte sur tous ces fichiers.
- L'OS trouve les fichiers et le CSS d'une app sous le nom de l'assemblage de son
  point d'entrée ; le SDK les publie sous `$(PackageId)`. Une app garde donc
  `PackageId` égal à `AssemblyName` (c'est le cas par défaut).
- Un projet Razor mis en paquet sans `staticwebassets.build.json` donne
  l'avertissement `HOSAPP0065` et un paquet limité aux `Assets` déclarés ; un
  chemin refusé par le manifeste donne `HOSAPP0064`.
- Un dépôt créé avec un modèle plus ancien met à jour son
  `publish-to-hack-store.yml` (§3), sinon le catalogue refuse sa version.
- Les apps déjà publiées doivent être remises en paquet avec la nouvelle version
  pour en profiter ; un ancien paquet marche comme avant.
- Le paquet reste lisible par un OS plus ancien, qui servira ses fichiers
  déclarés (tous, désormais) mais ne chargera pas son CSS isolé.

## Tâches

- [x] Outil `pack` : option `--web-assets`, lecture de `staticwebassets.build.json`, ajout des ressources au manifeste du paquet (`PackagedWebAssets`).
- [x] Cible `HackerOsPack` : passe le manifeste des ressources statiques.
- [x] Modèle `hackeros-solution` : `publish-to-hack-store.yml` copie le manifeste du paquet.
- [x] `InstalledAppAssetPublisher` : lien vers le CSS isolé (`linkStyleSheet` dans `installedAppAssets.js`).
- [x] Tests unitaires (`PackagedWebAssetsTests`, `InstalledAppAssetPublisherTests`).
- [x] Paquet de test `Tests/fixtures/installed-app` : JavaScript collocalisé et fichier non déclaré ; tests de bout en bout (`HackerOs.TrimmedPublish.E2E.Tests`) : module collocalisé et CSS isolé.
- [x] Docs : `app-installation.md`, `app-repository.md`, `features.md`, mémoire, entrée de changement.
