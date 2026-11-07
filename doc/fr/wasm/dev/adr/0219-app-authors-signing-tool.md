> [English version](../../../../en/wasm/dev/adr/0219-app-authors-signing-tool.md)

# ADR 0219 : paquets des dépôts d'apps, signés seulement dans le marketplace

## Statut

Accepté le 2026-10-31, chantier `todo/25-app-signing-tool` (série `todo/base`,
issue `TODO` [#321](https://github.com/Kazo-ca/Os/issues/321)).
Complète l'[ADR 0214](0214-hack-store-hot-add-signing-transactions.md) §2 pour
les sources `github:` ; ne change rien à la vérification faite par l'OS.

**Modifié le 2026-11-07** d'après la règle de Benoit du 2026-11-06 :
« Interdiction de signer les applications hors du repository de
marketplace. » Benoit a choisi de modifier cette ADR plutôt que d'en écrire une
nouvelle (voir « Questions posées à Benoit ») : l'outil de signature des
auteurs, décrit dans la première version, est retiré ; voir « Historique ».

## Contexte

- L'ADR 0214 §2 accepte un champ `signature` (ECDSA P-256, SHA-256, IEEE P1363
  en base64) pour chaque version de l'`index.json` d'un dépôt d'app, et des
  clés publiques dans `[signing-keys]` de `/etc/kazoos/app-sources.config`.
- Le Kazo OS Store compile et signe les apps dans `Kazo-ca/Os-AppMarketPlace`
  (`Kazo.Os.MarketPlace.Tool keygen` / `sign`, workflow `publish.yml`, secret
  `KAZO_STORE_SIGNING_KEY`) : c'est la seule signature que l'OS exige par
  défaut (`catalog:official`).
- Un auteur publie aussi son app sur la branche `kazoos-packages` de son
  dépôt (`github:<owner>/<repo>`), pour `kazopkg install <owner>/<repo>` sans
  le Store.
- Benoit veut une seule clé privée, gardée à un seul endroit : le dépôt du
  marketplace.

## Décision

### 1. Aucune signature hors de `Kazo-ca/Os-AppMarketPlace`

Seul le workflow `publish.yml` du dépôt du marketplace signe des paquets. Aucun
dépôt d'app, aucun modèle et aucun outil du SDK ne signe. Le secret
`HACK_STORE_TOKEN` des dépôts d'apps, qui ouvre la PR vers le catalogue (fork
et PR), ne signe rien et reste.

### 2. Le SDK n'a pas d'outil de signature (Q2 A)

- Le générateur de manifestes (`Kazo.Os.Tools.ManifestGenerator`, paquet
  `Kazo.Os.App.Build`) n'a pas de commande `keygen` ni `sign`.
- `Kazo.Os.Apps.targets` n'a pas de cible `KazoOsKeyGen` ni de propriété
  `KazoOsSigningKeyFile` ; `KazoOsPack` ne lit pas `HACKEROS_APP_SIGNING_KEY`
  et ne signe jamais.
- Les codes `KOSAPP0061` à `KOSAPP0063` (outil retiré) ne sont plus émis ;
  leurs numéros ne sont pas repris.

### 3. Index : les signatures sont gardées

`AppPackageWriter.AddToIndex` écrit le champ `signature` de chaque version qui
en a une : republier une version ou en ajouter une ne perd pas les signatures
des autres. Une version republiée perd la sienne (ses octets ont pu changer).

### 4. Branche `kazoos-packages` : workflow sans signature (Q3 A)

Le modèle `kazoos-solution` (et donc `Kazo-ca/Os-AppTemplate` et les dépôts
d'apps qui en viennent) a `.github/workflows/publish-packages.yml`, sur un tag
`v*` :

1. vérifie le sous-module et le tag (= version du manifeste), comme le workflow
   du Store ;
2. récupère la branche `kazoos-packages` (ou en crée une orpheline) dans un
   dossier, pour garder les versions déjà publiées ;
3. lance `KazoOsPack` avec `KazoOsPackageOutput` sur ce dossier, sans clé ;
4. pousse la branche. C'est le seul workflow du modèle avec
   `contents: write`.

Le paquet porte son SHA-256 seulement. L'OS traite cette source comme toute
source sans clé (ADR 0214 §2.4) : installation acceptée, approbation d'un
administrateur pour une source non fiable (ADR 0208).

### 5. Secrets et clés d'auteurs

- Les secrets `HACKEROS_APP_SIGNING_KEY` des dépôts d'apps n'ont plus d'usage :
  Benoit les supprime (Settings ▸ Secrets and variables ▸ Actions). Aucun agent
  ne manipule une clé privée.
- L'OS lit toujours `[signing-keys]` et `KazoOsAppSigningKeys` (ADR 0214 §2.3),
  mais les seules clés prévues sont celles des catalogues (`catalog:<nom>`).
  Une clé `github.com/<owner>` ajoutée par un administrateur ferait refuser les
  paquets non signés de ce dépôt : il faut la retirer.

## Conséquences

- Une seule clé privée à garder et à faire tourner : celle du catalogue.
- Un auteur ne peut pas prouver qu'un paquet de sa branche `kazoos-packages`
  vient de lui ; un utilisateur qui veut une app signée l'installe depuis le
  Kazo OS Store.
- Changement du paquet `Kazo.Os.App.Build` (outillage MSBuild, pas d'API C#
  publique) : un dépôt d'app qui appelait `KazoOsKeyGen` ou passait
  `KazoOsSigningKeyFile` doit retirer cet appel.

## Historique

La première version (2026-10-31) donnait à chaque auteur sa propre clé :
`keygen` et `sign` dans le générateur, cible `KazoOsKeyGen`, signature par
`KazoOsPack` avec `HACKEROS_APP_SIGNING_KEY` ou `KazoOsSigningKeyFile`
(avertissement `KOSAPP0063` sans clé), et clé de l'auteur dans
`[signing-keys]` (`github.com/<owner>=…`). Elle a été livrée dans Kazo OS
2.2.1 et retirée le 2026-11-07 (PR [#390](https://github.com/Kazo-ca/Os/pull/390)).

## Questions posées à Benoit

Première version, posées et répondues le 2026-10-31 :

| Question | Choix | Réponse de Benoit (2026-10-31) |
|---|---|---|
| Q1 Où mettre `keygen` et `sign` | **A outillage de build (recommandé)** ; B outil `dotnet` séparé ; C outil du MarketPlace | A (retiré le 2026-11-07) |
| Q2 Qui publie `kazoos-packages` | **A nouveau workflow `publish-packages.yml` (recommandé)** ; B dans le workflow du Store ; C à la main | A |
| Q3 Sans secret | **A publier non signé avec un avertissement (recommandé)** ; B échouer | A (sans objet le 2026-11-07 : jamais signé) |
| Q4 Nom du secret | **A `HACKEROS_APP_SIGNING_KEY` (recommandé)** ; B `HACK_STORE_SIGNING_KEY` | A (secret retiré le 2026-11-07) |

Modification, posées le 2026-11-07 dans le fil « Signer les apps seulement
dans le marketplace » :

| Question | Choix | Réponse de Benoit (2026-11-07) |
|---|---|---|
| Q1 Comment retirer la signature par dépôt | A nouvelle ADR qui remplace 0219 (recommandé) ; **B modifier l'ADR 0219** ; C supprimer l'ADR 0219 | B |
| Q2 Retirer aussi l'outil de signature du SDK | **A oui (recommandé)** ; B seulement les workflows | A |
| Q3 Garder la publication sur `kazoos-packages` | **A garder, sans signature (recommandé)** ; B supprimer le workflow | A |

## Tâches

- [x] `AddToIndex` garde les signatures.
- [x] Workflow `publish-packages.yml` du modèle `kazoos-solution`, `*.pem` ignoré.
- [x] `Kazo-ca/Os-AppTemplate` régénéré sur une version qui contient ce chantier.
- [x] Signature retirée du SDK, du modèle et des dépôts `Os-AppTemplate`, `Os-App-Webamp`, `Os-App-Chess` (2026-11-07).
- [ ] Benoit supprime les secrets `HACKEROS_APP_SIGNING_KEY` des dépôts d'apps.
