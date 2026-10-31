> [English version](../../../en/wasm/dev/releases.md)

# Publier une version de HackerOS

Une nouvelle version se publie en changeant **un seul fichier** :
`wasm2/HackerOs/build/Versions.props`. Une fois la modification fusionnée dans
`main`, GitHub Actions crée le tag, la GitHub Release et pousse les paquets sur
nuget.org ([ADR 0173](adr/0173-automatic-releases-from-the-version-file.md)).

## Le fichier de version

```xml
<HackerOsVersion>2.2.0</HackerOsVersion>
```

`Directory.Build.props` l'importe : chaque assembly, chacun des 42 paquets
`HackerOs.*`, les modèles `dotnet new`, les manifestes générés
(`sdkCompatibility.minimumVersion`) et `HackerOsSdk.Version` (affichée dans
Paramètres ▸ À propos et sur l'écran d'accueil) en découlent. Aucun code ni
test ne répète le numéro.

Choisir le numéro selon SemVer
([`sdk-packages.md`](../framework/sdk-packages.md#version-unique)) :

| Changement | Exemple |
| --- | --- |
| Correctif, aucune API publique ajoutée | `2.1.0` → `2.1.1` |
| Ajout dans l'API publique (`PublicAPI.Unshipped.txt` non vide) | `2.1.1` → `2.2.0` |
| Rupture du SDK (`*REMOVED*` dans un paquet du SDK) | `2.2.0` → `3.0.0` |
| Préversion (Release marquée « pre-release ») | `2.2.0-preview.1` |

## Publier, pas à pas

1. Créer une branche depuis `main`.
2. Pour une mineure ou une majeure, verser l'API publique :
   `build/tools/ship-public-api.sh` (`--check` pour voir sans rien changer).
3. Changer `HackerOsVersion` dans `build/Versions.props`.
   Dans la même PR, regrouper les entrées de `changes/unreleased/` dans
   `changes/<version>.json` ([`change-list.md`](change-list.md), ADR 0186).
4. (Conseillé) compiler une fois (`dotnet build HackerOs.sln -c Release`) et
   committer les manifestes réécrits ; le build les régénère de toute façon.
5. Ouvrir la PR vers `main`, attendre la vérification verte, fusionner.
6. Suivre l'onglet **Actions** : *Verify HackerOS WASM* tourne sur `main`, puis
   *Release HackerOS*. La Release apparaît sous **Releases** avec le tag
   `v<version>`.

## Ce que fait le workflow `Release HackerOS`

Fichier : `.github/workflows/release.yml`. Il démarre quand *Verify HackerOS
WASM* se termine **avec succès** après un push sur `main`, puis :

1. lit `HackerOsVersion` du commit vérifié ; si le tag `v<version>` existe
   déjà, il s'arrête (rien à publier) ;
2. empaquette avec `build/pack-local.sh --ci` ;
3. garde uniquement les `HackerOs.*.<version>.nupkg` et `.snupkg` ;
4. publie la PWA (`dotnet publish`) et la zippe en `hackeros-wasm-<version>.zip` ;
5. pousse les paquets sur nuget.org si c'est configuré (voir plus bas) ;
6. crée le tag et la GitHub Release : titre `HackerOS <version>`, notes
   générées par GitHub depuis les PR fusionnées, paquets et zip joints.

Le tag est créé en dernier : si une étape échoue, relancer le workflow
(*Re-run failed jobs*) reprend tout, et les paquets déjà présents sur nuget.org
sont sautés (`--skip-duplicate`).

### Lancer à la main

Onglet **Actions** ▸ *Release HackerOS* ▸ *Run workflow* (branche `main`) :

- sans option : publie la version de `main` si elle n'est pas encore taguée ;
- avec **republish** : ré-empaquette la version déjà taguée et la pousse sur
  nuget.org. Sert quand une Release a été créée avant la configuration de
  NuGet.

## Annonce aux sessions ouvertes

Le site publié contient `/version.json` (`{ "product": "HackerOS", "version": "2.1.0" }`),
écrit par le build depuis `build/Versions.props`. Chaque session ouverte le relit
toutes les 5 minutes ; quand le site sert une autre version, un bandeau dit
« HackerOS <version> est disponible » avec « Redémarrer pour mettre à jour »
([ADR 0174](adr/0174-announce-a-new-version-to-open-sessions.md)).

## Configurer la publication sur nuget.org

Deux possibilités ; la première est recommandée (aucune clé à stocker ni à
renouveler).

### Option A : Trusted Publishing (recommandé)

1. Se connecter à [nuget.org](https://www.nuget.org) avec le compte qui
   possédera les paquets.
2. Menu du compte (en haut à droite) ▸ **Trusted Publishing** ▸ **Create**
   (ou *Add new trusted publishing policy*).
3. Remplir :
   - *Policy name* : `HackerOs release` ;
   - *Package owner* : votre compte (ou l'organisation nuget.org qui
     possédera les paquets) ;
   - *Publisher* : **GitHub Actions** ;
   - *Repository owner* : `Kazo-ca` ;
   - *Repository* : `HackerOs` ;
   - *Workflow file* : `release.yml` (le nom seul, sans `.github/workflows/`) ;
   - *Environment* : laisser vide.
4. Dans GitHub : dépôt `Kazo-ca/HackerOs` ▸ **Settings** ▸ **Secrets and
   variables** ▸ **Actions** ▸ onglet **Variables** ▸ **New repository
   variable** : nom `NUGET_USER`, valeur = votre **nom d'utilisateur**
   nuget.org (le nom du profil, pas l'adresse courriel).

Le workflow appelle `NuGet/login@v1`, qui échange le jeton OIDC de GitHub
contre une clé valable une heure.

### Option B : clé d'API

1. nuget.org ▸ menu du compte ▸ **API Keys** ▸ **Create**.
2. *Key name* : `HackerOs GitHub Actions` ; *Expires in* : 365 jours ;
   *Select scopes* : **Push** ▸ *Push new packages and package versions* ;
   *Glob pattern* : `HackerOs.*`. Créer, puis **Copy**.
3. GitHub ▸ **Settings** ▸ **Secrets and variables** ▸ **Actions** ▸ onglet
   **Secrets** ▸ **New repository secret** : nom `NUGET_API_KEY`, valeur = la
   clé copiée.
4. Noter la date d'expiration : une clé expirée fait échouer l'envoi.

Si `NUGET_USER` existe, le workflow prend l'option A ; sinon `NUGET_API_KEY` ;
sans les deux, il publie la Release GitHub seule avec l'avertissement « nuget.org
publishing is not configured ».

### Facultatif : réserver le préfixe `HackerOs.`

Une réservation de préfixe affiche le badge « vérifié » et empêche un tiers de
publier un paquet `HackerOs.*`. Elle se demande par courriel à
`account@nuget.org` (voir la page *ID prefix reservation* de la documentation
NuGet), après la première publication.

## Vérifier une publication

- **Releases** du dépôt : la Release `HackerOS <version>`, ses `.nupkg`,
  `.snupkg` et `hackeros-wasm-<version>.zip`.
- Résumé du job *Release HackerOS* : la liste des paquets et la ligne
  « NuGet: packages pushed to nuget.org » (ou « not configured »).
- nuget.org : un paquet récent est indexé en quelques minutes
  (`https://www.nuget.org/packages/HackerOs.AppSdk`).

## Dépannage

| Symptôme | Cause et solution |
| --- | --- |
| *Release HackerOS* ne démarre pas | *Verify HackerOS WASM* a échoué ou a été annulé sur ce commit ; corriger, ou attendre le commit suivant de `main` (même version, pas encore taguée). |
| « already released » dans le résumé | Le tag `v<version>` existe : changer la version. |
| `403` à l'envoi NuGet | Clé expirée, mauvaise portée (glob), ou politique Trusted Publishing qui ne correspond pas au dépôt ou au fichier `release.yml`. |
| `NuGet/login` échoue | `NUGET_USER` ne correspond pas au nom du profil nuget.org, ou la politique n'existe pas. |

Voir aussi [`ci-workflows.md`](ci-workflows.md) et
[`../framework/sdk-packages.md`](../framework/sdk-packages.md).
