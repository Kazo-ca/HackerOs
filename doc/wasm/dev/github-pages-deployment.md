# Publication GitHub Pages : WASM et legacy

## Objet

Le dépôt public est [Kazo-ca/HackerOs](https://github.com/Kazo-ca/HackerOs).
La version WebAssembly autonome **2.0.3** est publiée à la racine de
`https://hackos.kazo.ca/` ; la version TypeScript d'origine est conservée sous
`https://hackos.kazo.ca/legacy/`. Voir l'[ADR 0167](adr/0167-combined-pages-deployment.md).

## Workflow et builds indépendants

`.github/workflows/deploy-to-pages.yml` est l'unique workflow qui déploie Pages.
Il s'exécute sur `main` quand les sources concernées, les fichiers de build ou
ce workflow changent, et peut être lancé manuellement.

| Sortie | Entrées de sa clé de cache | Construction si la clé n'existe pas |
| --- | --- | --- |
| `dist/` (legacy) | `src/**`, `package*.json`, `tsconfig*.json`, `webpack.config.js` | Node 20, `npm ci`, `npm run build:prod -- --output-public-path /legacy/` |
| `wasm2/HackerOs/artifacts/pages-publish/wwwroot/` | `wasm2/HackerOs/**`, hors `bin`, `obj`, `artifacts`, `node_modules`, `TestResults` | .NET 10, workload `wasm-tools`, `dotnet publish` de `OS/HackerOs.Ecosystem` en Release |

Les clés contiennent l'OS du runner et l'empreinte du contenu. Un cache exact
permet de réutiliser les octets publiés et de sauter toute la chaîne de build
concernée. Il n'y a pas de restauration par préfixe pouvant accepter une ancienne
version. Un changement legacy ne reconstruit donc pas le WASM, et inversement.
La première publication et un cache expiré, supprimé ou évincé reconstruisent
la sortie absente, même sans nouvelle modification de cette partie. Les fichiers
de build communs au legacy comptent aussi comme changements de cette version.
Modifier le préfixe `v1` d'une clé permet d'invalider volontairement son cache.

`deploy-wasm.yml` conserve la compilation, le scan Razor, l'audit des dépendances,
les tests sans navigateur et l'artefact WASM téléchargeable. Il ne déploie pas
Pages et ne demande pas les droits Pages/OIDC. Ses déclencheurs sont limités
au WASM et à son propre workflow.

Playwright n'est exécuté ni par Pages, ni par les vérifications automatiques
sur `main`. Les six projets de tests navigateur restent disponibles dans
`.github/workflows/verify-browser-tests.yml`, lancé uniquement par
**Actions → Verify HackerOS browser tests (manual) → Run workflow**. Ce workflow
conserve ses diagnostics et n'effectue aucun déploiement. Voir
[les suites de tests](building-and-testing.md#tester) pour les commandes locales.

### Tâches de séparation des tests

- [x] Publication Pages indépendante de Playwright.
- [x] Vérification automatique des 41 projets sans navigateur.
- [x] Six projets Playwright conservés dans un workflow manuel dédié.
- [x] Sélection des projets vérifiée avec `run-test-suite.ps1 -ListOnly`.

## Assemblage et routage

`build/assemble-pages.ps1` copie la publication WASM sans la modifier dans
`artifacts/pages/`, puis le bundle legacy dans `artifacts/pages/legacy/`. Il
ajoute `.nojekyll` et le `CNAME` `hackos.kazo.ca`. Le dossier de destination
doit être vide ; une entrée requise absente interrompt la publication.

La base HTML du WASM reste `/`. Le bundle legacy utilise le public path
Webpack `/legacy/`, y compris pour ses chunks et ses assets. La source LESS
conserve Google Fonts comme import CSS navigateur, sans télécharger la police
pendant la compilation.

Le manifeste d'intégrité PWA n'est pas réécrit et ne contient pas le legacy.
Le service worker WASM laisse les requêtes same-origin `/legacy` et `/legacy/**`
au réseau : aucune substitution par le shell WASM, aucun ajout de ses en-têtes
d'isolation au legacy. Les autres chemins gardent la stratégie offline existante.

## Informations visibles

- Connexion et création du premier compte : version du build et URL GitHub.
  Si le nom d'hôte est exactement `hackos.kazo.ca`, un message indique le
  déplacement de l'original vers `https://hackos.kazo.ca/legacy`. Les ports
  n'affectent pas ce test ; les sous-domaines ressemblants et les paramètres
  d'URL ne l'activent pas.
- Récupération et erreurs fatales du WASM : même référence au dépôt et à la version.
- Paramètres, À propos : version courante via `HackerOsSdk.Version` et URL GitHub.
- Paramètres legacy, About : URL GitHub visible, version legacy inchangée.
- Les métadonnées des paquets pointent aussi vers le nouveau dépôt. La version
  commune est définie une seule fois dans `build/Versions.props` (`2.0.3`).

## Vérification locale

Depuis la racine du dépôt, sur un dossier de sortie encore vide :

```powershell
npm ci
npm run build:prod -- --output-public-path /legacy/
dotnet publish wasm2/HackerOs/OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj -c Release -m:1 -o wasm2/HackerOs/artifacts/pages-publish
./wasm2/HackerOs/build/assemble-pages.ps1

$env:HACKEROS_PAGES_ROOT = (Resolve-Path wasm2/HackerOs/artifacts/pages).Path
dotnet test wasm2/HackerOs/Tests/HackerOs.Pwa.E2E.Tests/HackerOs.Pwa.E2E.Tests.csproj --filter FullyQualifiedName~PublishedPagesTests
```

Le test sert l'artefact statique, active le vrai service worker, crée un compte,
vérifie À propos, ouvre le legacy et son écran About, puis le recharge. Sans
`HACKEROS_PAGES_ROOT`, ce test spécifique est ignoré lors des tests ordinaires.
`ProjectInformationTests` couvre les domaines autorisés et refusés, et
`ResourceParityTests` vérifie les traductions anglais/français.

## Tâches

- [x] Garder un seul éditeur de l'artefact Pages.
- [x] Séparer les clés et conditions de build des deux versions.
- [x] Publier WASM à la racine et legacy sous `/legacy/`.
- [x] Mettre à jour version et références au dépôt.
- [x] Limiter le message de déplacement au domaine demandé.
- [x] Terminer la validation du navigateur sur l'artefact combiné.

Validation du 2026-10-24 : publication Release WASM réussie, build legacy réussi
(avertissements de taille du bundle existants), 103 tests ciblés d'information
produit/traductions réussis et parcours réel `PublishedPagesTests` réussi après
activation du service worker. Les workflows ont été analysés en YAML ; les
conditions indépendantes, l'unicité du déploiement et la conservation des octets
du manifeste PWA ont été contrôlées localement. Le premier déploiement distant
est déclenché par le prochain push sur `main`, ou manuellement sur GitHub.
