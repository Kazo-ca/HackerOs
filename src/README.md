# HackerSimulator legacy (TypeScript)

Ce dossier contient la **première version** de HackerSimulator, écrite en
TypeScript et LESS et empaquetée par Webpack. Elle est gardée **en lecture
seule** comme référence de comportement : tout le travail actif se fait dans la
version WebAssembly, [`wasm2/HackerOs/`](../wasm2/HackerOs/README.md), documentée
dans [`doc/en/wasm/`](../doc/en/wasm/README.md).

La version legacy est publiée sur <https://hackos.kazo.ca/legacy/> par le même
workflow GitHub Pages que la version WebAssembly
([`github-pages-deployment.md`](../doc/en/wasm/dev/github-pages-deployment.md)).

## Compiler et lancer

Prérequis : Node.js 20 et npm. Les commandes se lancent à la racine du dépôt.

```bash
npm install
npm run dev          # serveur de développement avec rechargement à chaud
npm run build:debug  # bundle de développement dans dist/
npm run build:prod   # bundle minifié dans dist/
npm run tsc:debug    # compile chaque fichier TypeScript avec ses source maps
```

## Structure

| Dossier | Contenu |
| --- | --- |
| [`index.ts`](index.ts), [`index.html`](index.html) | Point d'entrée et page hôte. |
| [`core/`](core/) | Noyau : système de fichiers, processus, fenêtres, bureau, menu Démarrer, thèmes, réglages, réseau simulé, moniteurs multiples. |
| [`apps/`](apps/) | Applications : terminal, éditeur de code, éditeur de texte, explorateur de fichiers, navigateur, calculatrice, moniteur système, Hack Paint, réglages, éditeur de thèmes, visionneuse des journaux d'erreurs. |
| [`commands/`](commands/) | Interpréteur de commandes du terminal et commandes Linux (`ls`, `cat`, `grep`, `ps`, `curl`, `nmap`, `ping`…). |
| [`websites/`](websites/) | Sites web simulés servis au navigateur intégré. |
| [`styles/`](styles/) | Feuilles LESS communes. |
| [`docs/`](docs/README.md) | Documentation des fonctionnalités de la version legacy. |

## Correspondance avec la version WebAssembly

Les apps et commandes de ce dossier ont été portées en C# dans
`wasm2/HackerOs/Apps/`. Les rapports de migration archivés sont dans
[`doc/en/wasm/dev/old/migration/`](../doc/en/wasm/dev/old/migration/README.md).
