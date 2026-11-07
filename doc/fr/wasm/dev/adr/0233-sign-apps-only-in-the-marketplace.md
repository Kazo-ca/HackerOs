> [English version](../../../../en/wasm/dev/adr/0233-sign-apps-only-in-the-marketplace.md)

# ADR 0233 : les apps sont signées seulement dans le dépôt du marketplace

## Statut

Acceptée le 2026-11-07, d'après la règle de Benoit du 2026-11-06 : « Interdiction
de signer les applications hors du repository de marketplace. » Les trois
questions de mise en œuvre sont dans « Questions posées à Benoit ».

- **Remplace :** l'[ADR 0219](0219-app-authors-signing-tool.md) en entier
  (outil de signature des auteurs, secret `HACKEROS_APP_SIGNING_KEY`, clé de
  l'auteur dans `[signing-keys]`), sauf son §2 (`index.json` garde les
  signatures déjà présentes) et la publication de la branche `kazoos-packages`
  (son §3, sans la signature).
- **Ne change pas :** la vérification des signatures par l'OS et la clé du
  catalogue officiel ([ADR 0214](0214-hack-store-hot-add-signing-transactions.md)
  §2) ; l'outil `Kazo.Os.MarketPlace.Tool` (`keygen`, `sign`) et le secret
  `KAZO_STORE_SIGNING_KEY` du dépôt `Kazo-ca/Os-AppMarketPlace` ; le secret
  `HACK_STORE_TOKEN` des dépôts d'apps, qui ouvre la PR vers le catalogue
  (fork et PR, aucune signature).

## Contexte

- L'ADR 0219 a donné aux auteurs d'apps leur propre clé : `KazoOsKeyGen` crée la
  paire, `KazoOsPack` signe chaque paquet avec `HACKEROS_APP_SIGNING_KEY`, et le
  workflow `publish-packages.yml` du modèle `kazoos-solution` pousse le paquet
  signé sur la branche `kazoos-packages` du dépôt de l'app.
- Ce workflow est dans `Kazo-ca/Os-AppTemplate`, `Kazo-ca/Os-App-Webamp` et
  `Kazo-ca/Os-App-Chess` : une clé privée par dépôt d'app, gardée dans un secret
  de chaque dépôt.
- Le Kazo OS Store, lui, compile et signe les apps dans
  `Kazo-ca/Os-AppMarketPlace` (`publish.yml`, ADR 0214 §2.5) : c'est la seule
  signature que l'OS exige par défaut (`catalog:official`).
- Benoit veut une seule clé, gardée à un seul endroit : le dépôt du
  marketplace.

## Décision

### 1. Aucune signature hors de `Kazo-ca/Os-AppMarketPlace`

Seul le workflow `publish.yml` du dépôt du marketplace signe des paquets, avec
`Kazo.Os.MarketPlace.Tool` et le secret `KAZO_STORE_SIGNING_KEY`. Aucun dépôt
d'app, aucun modèle et aucun outil du SDK ne signe.

### 2. L'outil de signature quitte le SDK (Q2 A)

- Le générateur de manifestes (`Kazo.Os.Tools.ManifestGenerator`, paquet
  `Kazo.Os.App.Build`) perd les commandes `keygen` et `sign` et la classe
  `PackageSigner`.
- `Kazo.Os.Apps.targets` perd la cible `KazoOsKeyGen` et la propriété
  `KazoOsSigningKeyFile` ; `KazoOsPack` ne lit plus `HACKEROS_APP_SIGNING_KEY`.
- Les codes `KOSAPP0061` (propriété de `KazoOsKeyGen`), `KOSAPP0062` (signature
  impossible) et `KOSAPP0063` (paquet non signé) ne sont plus émis ; leurs
  numéros ne sont pas repris.
- `AppPackageWriter.AddToIndex` garde le champ `signature` d'une version qui en
  a déjà un (ADR 0219 §2) : un index écrit par une version précédente ne perd
  rien.

### 3. `kazoos-packages` reste publiée, sans signature (Q3 A)

Le workflow `publish-packages.yml` du modèle `kazoos-solution` (et des dépôts
d'apps qui l'ont) garde son rôle : sur un tag `v*`, il pousse le paquet sur la
branche `kazoos-packages`, pour `kazopkg install <owner>/<repo>`. Il n'a plus de
secret ni d'avertissement : le paquet porte son SHA-256 seulement. L'OS traite
cette source comme toute source sans clé (ADR 0214 §2.4) : installation
acceptée, approbation d'un administrateur pour une source non fiable (ADR 0208).

### 4. Les secrets `HACKEROS_APP_SIGNING_KEY` sont à supprimer

Les dépôts d'apps n'en ont plus l'usage. Benoit supprime le secret de chaque
dépôt d'app qui l'a (Settings ▸ Secrets and variables ▸ Actions) ; aucun
agent ne manipule une clé privée.

### 5. Les clés `github.com/<owner>` de `[signing-keys]`

L'OS lit toujours `[signing-keys]` et `KazoOsAppSigningKeys` (ADR 0214 §2.3),
mais la documentation ne propose plus d'y mettre la clé d'un auteur : les
seules clés prévues sont celles des catalogues (`catalog:<nom>`). Une clé
`github.com/<owner>` déjà ajoutée par un administrateur ferait refuser les
paquets non signés de ce dépôt : il faut la retirer.

## Conséquences

- Une seule clé privée à garder et à faire tourner : celle du catalogue.
- Un auteur ne peut plus prouver qu'un paquet de sa branche `kazoos-packages`
  vient de lui ; un utilisateur qui veut une app signée l'installe depuis le
  Kazo OS Store.
- Changement du paquet `Kazo.Os.App.Build` (outillage MSBuild, pas d'API C#
  publique) : un dépôt d'app qui appelait `KazoOsKeyGen` ou passait
  `KazoOsSigningKeyFile` doit retirer cet appel ; sans lui, `KazoOsPack`
  fonctionne comme avant, non signé.
- Les workflows de `Kazo-ca/Os-AppTemplate`, `Kazo-ca/Os-App-Webamp` et
  `Kazo-ca/Os-App-Chess` perdent l'étape de signature (une PR par dépôt ; Chess
  est en production, seul son workflow change).

## Questions posées à Benoit

Posées le 2026-11-07 dans le fil « Signer les apps seulement dans le
marketplace », avec les recommandations (A) mises en œuvre en attendant.

| Question | Choix | Réponse de Benoit |
|---|---|---|
| Q1 Comment retirer la signature par dépôt de l'ADR 0219 | **A nouvelle ADR qui remplace 0219 (recommandé)** ; B modifier l'ADR 0219 ; C supprimer l'ADR 0219 | en attente |
| Q2 Retirer aussi l'outil de signature du SDK | **A oui (recommandé)** ; B seulement les workflows | en attente |
| Q3 Garder la publication sur `kazoos-packages` | **A garder, sans signature (recommandé)** ; B supprimer le workflow | en attente |

## Tâches

- [x] ADR 0233 (fr, en), statut de l'ADR 0219, index des ADR.
- [x] `keygen`, `sign`, `PackageSigner`, `KazoOsKeyGen` et la signature de `KazoOsPack` retirés ; tests mis à jour.
- [x] Workflow `publish-packages.yml` et readmes du modèle `kazoos-solution` sans signature.
- [x] Documentation (`app-installation.md`, `framework/app-repository.md`, `framework/sdk-packages.md`, `features.md`, mémoire).
- [x] PR des dépôts `Os-AppTemplate`, `Os-App-Webamp` et `Os-App-Chess` (workflow sans signature).
- [ ] Benoit supprime les secrets `HACKEROS_APP_SIGNING_KEY` des dépôts d'apps.
