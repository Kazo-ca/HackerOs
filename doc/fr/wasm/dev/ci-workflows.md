> [English version](../../../en/wasm/dev/ci-workflows.md)

# Validations GitHub Actions

Ce que vérifient les workflows de `.github/workflows/`, quand ils tournent, et
comment lire un échec. Pour lancer les mêmes vérifications sur sa machine, voir
[`building-and-testing.md`](building-and-testing.md). La publication du site est
détaillée dans [`github-pages-deployment.md`](github-pages-deployment.md).

## Les workflows

| Workflow (fichier) | Déclencheurs | Ce qu'il vérifie |
| --- | --- | --- |
| **Verify HackerOS WASM** (`deploy-wasm.yml`) | Toute PR qui touche `wasm2/HackerOs/**` (sauf `wasm2/HackerOs/docs/**`) ou ce workflow ; push sur `main` et sur `*/base` ; manuel | Paquets locaux Windowing/Taskbar, restauration, build Release (avertissements en erreurs), scan Razor (pas de `<style>`, `<script>` ni gestionnaire `on*` en ligne), paquets vulnérables, tous les tests sans navigateur, publication de la PWA. |
| **Verify local Windowing and Taskbar packages** (`window-taskbar-packages.yml`) | PR ou push (`main`, `*/base`) qui touche les projets de fenêtrage, de thème, d'icônes, de barre des tâches ou l'exemple SampleHost | Paquets et symboles produits dans l'ordre des dépendances, exemple restauré **uniquement** depuis ces paquets, compilé et publié. |
| **Deploy to GitHub Pages** (`deploy-to-pages.yml`) | Push sur `main` (déploie) ; PR qui touche le legacy (`src/**`, `package*.json`, `tsconfig*.json`, `webpack.config.js`), `assemble-pages.ps1` ou ce workflow (construit sans déployer) ; manuel | Build legacy pour `/legacy/`, publication WASM, assemblage du site combiné. Une PR a son propre groupe de concurrence : elle n'annule jamais un déploiement de `main`. |
| **Verify HackerOS browser tests** (`verify-browser-tests.yml`) | Chaque nuit sur `main` (06:17 UTC), **seulement si `main` a changé depuis le dernier commit testé** ; manuel (tourne toujours) | Les six projets Playwright, avec le Chromium installé par Playwright. Un échec la nuit ouvre ou complète une issue ([section dédiée](#tests-navigateur-chaque-nuit)). |
| **Release HackerOS** (`release.yml`) | Fin réussie de *Verify HackerOS WASM* après un push sur `main` ; manuel (option `republish`) | Publie une version dont le tag `v<version>` n'existe pas encore : paquets `HackerOs.*`, site WASM zippé, envoi sur nuget.org, tag et GitHub Release ([`releases.md`](releases.md), ADR 0173). |
| **Change list entry** (`changelog-entry.yml`) | Toute PR vers `main` (ouverte, poussée, étiquette ajoutée ou retirée) | Une PR qui touche le produit (`wasm2/HackerOs` hors `Tests/`, `docs/`, `changes/` et fichiers `.md`) ajoute au moins une entrée `wasm2/HackerOs/changes/unreleased/<guid>.json`, sauf avec l'étiquette `no-changelog` ([`change-list.md`](change-list.md), ADR 0186). Le format des entrées est vérifié par `ChangeEntriesTests` dans *Verify HackerOS WASM*. |

Les PR vers `main` comme vers une branche d'intégration `<série>/base` sont
vérifiées : le déclencheur `pull_request` n'a pas de filtre de branche. Le
push sur `*/base` vérifie aussi l'état de la branche après chaque fusion d'un
chantier.

Une PR qui ne touche que la doc (`doc/**`, `*.md` à la racine) ne lance que
*Change list entry* (vers `main`), qui passe tout de suite puisqu'elle ne touche pas
le produit. Les `README.md` des projets sous `wasm2/HackerOs` restent des
entrées du build (paquets NuGet, `AnalyzerReleases.*.md`) : les modifier lance
la vérification.

## Tests instables et nouvel essai

`build/run-test-suite.ps1 -RetryFailedTests N` relance, après l'échec d'un
projet, **seulement ses tests en échec** (lus dans le `.trx`), jusqu'à `N`
fois. Les workflows passent `-RetryFailedTests 1` :

- un test qui réussit au nouvel essai ne fait pas échouer la vérification ; il
  apparaît comme avertissement « Flaky test » sur la PR et dans le résumé du job
  (« Tests instables ») ;
- un test qui échoue deux fois fait échouer la vérification ;
- un projet en échec sans test en échec dans son `.trx` (plantage, délai du
  runner) n'est jamais relancé et fait échouer la vérification.

Les résultats du premier passage restent dans
`artifacts/test-results/<suite>/<projet>/`, ceux du nouvel essai dans
`retry-1/` à côté ; l'artefact `hackeros-verification-<essai>` les contient.

Sans le paramètre (par défaut en local), le script garde son comportement
strict : un échec est un échec. Les tests instables connus sont listés dans
[`pitfalls.md`](pitfalls.md#4-tests-et-e2e) ; en voir un passer au nouvel
essai sur une PR n'est pas une régression, mais un test qui y apparaît souvent
mérite d'être stabilisé.

## Lire un échec

1. Ouvrir le job `verify` et chercher `Test projects failed:` : le message
   nomme les projets en échec. Le résumé du job liste aussi les échecs et les
   tests instables.
2. Télécharger l'artefact `hackeros-verification-<essai>` pour les `.trx`.
3. Reproduire en local avec la même commande
   (`./build/run-test-suite.ps1 -Suite NonBrowser`) après un build Release.

Un build et des tests locaux verts restent exigés avant de fusionner : la
vérification GitHub des PR ne lance pas les suites Playwright (elles tournent la
nuit sur `main`, voir ci-dessous).

## Tests navigateur chaque nuit

Décision de Benoit (2026-10-30, issue #292) : les six projets Playwright
tournent **chaque nuit sur `main`**, pas à chaque PR, parce que des tests
navigateur instables connus
([`known-test-failures.md`](../memory/known-test-failures.md)) bloqueraient les
PR. Le workflow `verify-browser-tests.yml` a trois jobs :

1. **`changes`** : un lancement planifié cherche le dernier lancement de ce
   workflow sur `main` qui a fini ses tests (réussi ou en échec ; un lancement
   annulé ou hors délai ne compte pas) et compare son commit à celui de `main`.
   S'ils sont égaux, `main` n'a pas changé : les tests sont sautés et le résumé
   du job le dit. Un lancement manuel teste toujours.
2. **`browser-tests`** : build Release, Chromium de Playwright, puis
   `run-test-suite.ps1 -Suite Browser -RetryFailedTests 1` (un test qui passe
   au second essai est signalé « Flaky test », voir plus haut). L'artefact
   `hackeros-browser-tests-<essai>` garde les `.trx` et les traces 14 jours.
3. **`report`** (seulement sur `main`) : un échec ouvre l'issue « Les tests
   navigateur de la nuit échouent sur main », ou y ajoute un commentaire si
   elle est déjà ouverte (lien du lancement et commit). Le premier lancement
   vert suivant la ferme avec un commentaire.

Un échec reste donc visible trois fois : le statut rouge du workflow,
l'issue ouverte (avec sa notification) et le commit de `main` concerné. Si
`main` ne bouge pas après un échec, la nuit suivante ne relance pas les tests :
l'issue reste ouverte jusqu'au prochain commit testé vert. Pour retester sans
commit, lancer le workflow à la main sur `main` (Actions ▸ Verify HackerOS
browser tests ▸ Run workflow).

GitHub ne lance un `schedule` que depuis la branche par défaut : le
déclencheur nocturne compte à partir de son arrivée dans `main`.

## Versions des actions

Les actions officielles sont aux versions qui tournent sous Node 24
(`checkout@v6`, `setup-dotnet@v5`, `setup-node@v6`, `cache@v5`,
`upload-artifact@v6`, `configure-pages@v6`, `upload-pages-artifact@v5`,
`deploy-pages@v5`) : les runners forcent Node 24 et signalaient les versions
Node 20 comme dépréciées.

## Installation du SDK .NET

Les cinq workflows installent le SDK par l'action locale
`.github/actions/setup-dotnet-sdk`. Elle lit la dernière version du canal
(`latest-sdk` de
`https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`)
et la passe **exacte** à `actions/setup-dotnet`.

Pourquoi : avec `dotnet-version: 10.0.x`, le script d'installation suit le lien
`aka.ms` du canal, qui pointait le 2026-10-25 vers
`dotnetorigin.blob.core.windows.net`, un stockage qui répondait HTTP 409 ; le
job échouait avant toute compilation. Avec une version exacte, le script
télécharge depuis `builds.dotnet.microsoft.com`, qui répondait normalement. Si
le fichier de métadonnées est illisible, l'action se replie sur `10.0.x` avec un
avertissement. `wasm2/HackerOs/global.json` (`10.0.100`, `latestFeature`)
accepte toute version 10.0 plus récente.

## Contrôles obligatoires

Rendre un workflow obligatoire se règle dans GitHub (Settings ▸ Branches ▸
règle de protection de `main` ou de `*/base`), pas dans le dépôt. Attention :
un workflow filtré par chemins ne tourne pas sur une PR qui ne touche pas ces
chemins, et un contrôle obligatoire jamais lancé bloque la PR en « attente ».
