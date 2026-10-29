> [English version](../../../../en/wasm/dev/adr/0188-hack-store-and-app-catalog.md)

# ADR 0188 : le Hack Store et le catalogue `HackerOs-AppMarketPlace`

## Statut

Accepté le 2026-10-29 par Benoit, après deux séries de questions dans le fil
« Planifier le Hack Store » (réponses en fin de document). Aucun code n'est
écrit par cette ADR : elle fixe la cible, et les chantiers sont listés dans
[`../plans/hackeros-store.md`](../plans/hackeros-store.md). Complète
l'[ADR 0182](0182-install-apps-from-github.md) (paquets `.hackapp`, `hackpkg`)
et l'[ADR 0176](0176-app-repository-template.md) (dépôt template d'apps). La
confiance et les droits d'installation sont dans
l'[ADR 0189](0189-app-sources-trust-and-per-user-install.md), les packs de
thèmes (v2) dans l'[ADR 0190](0190-theme-packs.md).

## Contexte

- `hackpkg` (ADR 0182) installe une app depuis la branche `hackeros-packages`
  de son dépôt GitHub, lue par `raw.githubusercontent.com`. Il n'y a ni liste
  d'apps, ni interface graphique.
- Le dépôt [`Kazo-ca/HackerOs-AppMarketPlace`](https://github.com/Kazo-ca/HackerOs-AppMarketPlace)
  existe et est vide.
- Le menu Démarrer a une section **Épinglées** (l'« accès rapide ») ; un
  nouveau profil n'y a aucune app aujourd'hui.
- Benoit veut que tout ce qui est publié dans notre catalogue soit relu avant
  d'être approuvé et ne dépende d'aucun binaire compilé par un tiers.

## Décision

### 1. L'app système Hack Store

- App système `org.hackeros.store`, nom affiché **Hack Store**, dans le dépôt
  HackerOs, livrée avec l'OS et **non désinstallable** (comme Paramètres).
- Elle est **épinglée par défaut** dans la section Épinglées du menu Démarrer :
  pour tout nouveau profil, et ajoutée **une fois** aux profils existants par
  une migration (un drapeau dans le fichier du menu Démarrer évite de la
  rajouter après un désépinglage). L'utilisateur peut la désépingler.
- Elle marche sur le bureau et sur l'interface mobile (tactile).

### 2. Contenu de la v1

- Accueil par catégories, recherche (nom, description courte, étiquettes).
- Fiche d'une app : icône, captures, description, version, taille, licence,
  capacités demandées, source et niveau de confiance (ADR 0189).
- Boutons Installer (pour moi ou pour le système, ADR 0189), Ouvrir, Mettre à
  jour, Désinstaller ; onglet **Installées**.
- Pas de notes ni d'avis (il faudrait un serveur).
- Seulement les apps externes : les apps intégrées à l'OS restent gérées dans
  Paramètres ▸ Apps (ADR 0048).
- Une installation demande un **redémarrage de l'OS** pour que l'app apparaisse
  (ADR 0182, Q5 C). Le Store regroupe les installations en attente et propose
  « Redémarrer pour terminer ». L'ajout à chaud reste un chantier séparé.

### 3. Mises à jour

Le Store vérifie les mises à jour au démarrage de la session puis toutes les
6 heures (réglable dans `/etc/hackeros/app-sources.config`, clé
`[updates] interval-hours`). S'il en trouve, il met un badge sur son icône et
envoie une notification ; la mise à jour se fait en un clic, avec les mêmes
droits que l'installation (ADR 0189). Pas de mise à jour automatique.

### 4. Le dépôt catalogue `HackerOs-AppMarketPlace`

Branche `main`, relue par pull request : **un dossier par version d'app**,
nommé `<appId>_<version>`.

```text
apps/
  org.example.myapp_1.1.2/
    icon.png                     PNG 256×256, 200 Ko au plus
    short_description.en.txt     120 caractères au plus, anglais obligatoire
    short_description.fr.txt     facultatif (une langue = un suffixe)
    description.en.md            anglais obligatoire
    description.fr.md            facultatif
    manifest.json                le manifeste généré de l'app (ADR 0047)
    store.json                   voir ci-dessous
    screenshots/1.png … 5.png    facultatif, 1 à 5 captures PNG
categories.json                  liste fermée des catégories, libellés fr et en
.github/pull_request_template.md
.github/workflows/validate.yml   vérifie une PR
.github/workflows/publish.yml    publie après la fusion
```

`store.json` :

```json
{
  "repository": "https://github.com/example/HackerOs-App-MyApp",
  "commit": "3f2a9c1…",
  "category": "productivity",
  "tags": ["notes", "markdown"],
  "license": "MIT"
}
```

- `commit` est l'identifiant complet du commit de cette version dans le dépôt
  de l'app. L'auteur **ne fournit pas de binaire** : notre CI compile l'app
  elle-même depuis ce commit et publie **notre** paquet. Ce qu'on publie est
  donc exactement le code relu.
- Les étiquettes sont libres ; la catégorie vient de `categories.json`.

### 5. Vérification d'une PR (`validate.yml`)

Sur chaque PR, l'action :

1. vérifie le format du dossier (noms, tailles, dimensions de l'icône,
   longueur de la description courte, anglais présent, catégorie connue) ;
2. vérifie que `manifest.json` passe les contrôles de l'ADR 0098 et que son
   `appId` et sa version correspondent au nom du dossier ;
3. vérifie que le commit existe dans le dépôt indiqué ;
4. compile l'app à ce commit (`dotnet build -c Release -t:HackerOsPack`,
   ADR 0182) et vérifie que le manifeste produit est celui du dossier ;
5. fait la vérification des API contre le runtime élagué (ADR 0182, Q1) ;
6. lance une analyse CodeQL du code de l'app ;
7. écrit en commentaire de la PR les capacités demandées (les dangereuses en
   évidence) et la liste des fichiers JavaScript embarqués.

Benoit (ou un mainteneur qu'il désigne) relit le code et approuve ; rien n'est
fusionné automatiquement.

### 6. Publication (`publish.yml`)

Après la fusion, l'action recompile l'app au même commit et met à jour une
branche séparée **`packages`**, sans historique de code, lue par
`raw.githubusercontent.com` (les releases GitHub sont bloquées par CORS,
ADR 0182) :

```text
catalog.json
packages/<appId>/<version>/<appId>-<version>.hackapp
assets/<appId>/<version>/icon.png, screenshots/…, description.*.md
```

`catalog.json` liste, par app : `appId`, `kind` (`app` ou `theme-pack`,
ADR 0190), nom, catégorie, étiquettes, descriptions courtes, licence, dépôt,
capacités, `sdkCompatibility`, et ses versions publiées (chemin du paquet,
SHA-256, taille, commit). Les **3 dernières versions** de chaque app restent
téléchargeables ; les plus anciennes sont retirées de `packages` (leur dossier
reste dans l'historique de `main`).

### 7. L'action des créateurs d'apps

Le modèle `hackeros-solution` (ADR 0176) reçoit un workflow : sur un tag `v*`,
il compile l'app, produit le dossier `<appId>_<version>` (icône, descriptions et
`store.json` pris dans un dossier `store/` du dépôt de l'app, commit du tag) et
ouvre la PR vers `HackerOs-AppMarketPlace` depuis un fork de l'auteur, avec un
jeton que l'auteur met dans ses secrets. Sans jeton, le dossier est laissé en
artefact du workflow, à soumettre à la main.

### 8. `hackpkg` et le catalogue

`hackpkg` lit les mêmes catalogues que le Store : `hackpkg install <appId>`
cherche l'app dans les sources de catalogue (ADR 0189), en plus de
`github:owner/repo` (ADR 0182). Le Store et `hackpkg` passent par le même
service d'installation.

## Conséquences

- Un auteur publie par une PR ; le code est relu et compilé par nous avant
  d'arriver chez les utilisateurs.
- Le dépôt catalogue a deux branches de natures différentes : `main` (petits
  fichiers relus) et `packages` (fichiers générés, réécrits par l'action).
- Le Store dépend de trois chantiers préalables : le modèle `hackeros-solution`
  (ADR 0176), le service des ressources `wwwroot/` des apps installées
  (limite de l'ADR 0182) et la confiance par source (ADR 0189).
- Les paquets ne sont toujours pas signés : `catalog.json` porte les SHA-256,
  et la branche `packages` n'est écrite que par notre action.

## Réponses de Benoit

Questions posées le 2026-10-28 et le 2026-10-29. Sans réponse ou « ok » = la
recommandation.

- **Q1** App système dans le dépôt HackerOs, non désinstallable : A.
- **Q2** Épinglée par défaut, ajoutée une fois aux profils existants,
  désépinglable : A.
- **Q3** Catalogue : un dossier par version `<appId>_<version>` avec icône,
  descriptions, manifeste et un JSON (dépôt, commit, étiquettes, catégorie) ;
  modèle de PR ; action qui vérifie ; action qui met à jour `catalog.json` et le
  paquet compilé après acceptation ; action pour les créateurs (proposition de
  Benoit).
- **Q6** Redémarrage après installation en v1 : A. **Q7** Vérification au
  démarrage et périodique, badge, mise à jour en un clic : A. **Q8** Contenu de
  la v1 sans notes ni avis : A. **Q9** Apps externes seulement : A.
- **Q17** Bureau et mobile : A.
- **Q18** Notre CI compile depuis le commit, l'auteur ne fournit pas de
  binaire : A. **Q19** Branche `packages` : A. **Q20** 3 dernières versions : A.
  **Q21** Contenu du dossier (icône 256×256, descriptions fr/en avec anglais
  obligatoire, captures facultatives) : A. **Q22** Vérifications avec CodeQL et
  approbation de Benoit : A. **Q23** Action des créateurs qui ouvre la PR depuis
  un fork : A.

## Tâches

- [ ] Modèle `hackeros-solution` (tâches de l'ADR 0176), avec le workflow du §7.
- [ ] Servir les ressources `wwwroot/` des apps installées (limite de l'ADR 0182).
- [ ] Dépôt `HackerOs-AppMarketPlace` : `categories.json`, modèle de PR,
      `validate.yml`, `publish.yml`, branche `packages`, `catalog.json`.
- [ ] App système Hack Store (v1, §1 à §3), épinglage par défaut et migration.
- [ ] `hackpkg install <appId>` depuis les catalogues.
- [ ] Documentation : `app-installation.md`, guide de publication pour les auteurs.
