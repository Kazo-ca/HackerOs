> [English version](../../../../en/wasm/dev/adr/0173-automatic-releases-from-the-version-file.md)

# ADR 0173 : publication automatique à partir du fichier de version

## Statut

Accepté le 2026-10-26 à la demande de Benoit (« changer juste 1 fichier et que
toutes les assembly changent de version ; quand la version change vers `main`,
déployer les paquets NuGet et le reste dans les releases du dépôt »). Remplace
la règle « jamais nuget.org » de l'[ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) §2 ;
le reste de l'ADR 0098 (version unique, paquets, flux local) tient toujours.

## Contexte

- `wasm2/HackerOs/build/Versions.props` (`HackerOsVersion`) donne déjà sa
  version à chaque projet (ADR 0098 §1) : assemblies, 42 paquets `HackerOs.*`,
  modèles, manifestes générés, `HackerOsSdk.Version` affichée par l'OS.
- Une publication restait manuelle : aucun tag, aucune GitHub Release, paquets
  produits seulement dans `artifacts/local-nupkg`.
- Un seul endroit codait encore le numéro en dur : le test
  `PublishedPagesTests` de `HackerOs.Pwa.E2E.Tests` (`"2.0.3"`).

## Décision

1. **Un seul fichier** : publier une version, c'est changer `HackerOsVersion`
   dans `build/Versions.props` et fusionner dans `main`. Aucun code ni test ne
   répète le numéro : le test de la PWA lit `HackerOsVersion` par un
   `AssemblyMetadata` de son projet. Les manifestes générés (suivis par git
   pour la revue) sont réécrits par le build ; les committer avec la montée de
   version est conseillé, pas nécessaire.
2. **Workflow `Release HackerOS`** (`.github/workflows/release.yml`), lancé
   quand **Verify HackerOS WASM** réussit sur un push de `main` : il lit la
   version du commit vérifié et, si le tag `v<version>` n'existe pas,
   - empaquette par `build/pack-local.sh --ci` (`ContinuousIntegrationBuild=true`) ;
   - garde seulement les `HackerOs.*.<version>.nupkg` et `.snupkg` (jamais une
     app de compatibilité `Contoso.*`) ;
   - publie le site WASM et le zippe (`hackeros-wasm-<version>.zip`) ;
   - pousse les paquets sur nuget.org (`--skip-duplicate`) ;
   - crée **en dernier** le tag et la GitHub Release (notes générées par
     GitHub, paquets et zip joints ; préversion si la version a un suffixe
     `-…`).
   Le tag sert de marque « déjà publiée » : un commit dont la version est déjà
   taguée ne publie rien. Un échec avant le tag se reprend en relançant le
   workflow.
3. **Authentification nuget.org** : *Trusted Publishing* (OIDC,
   `NuGet/login@v1`, clé valable une heure) quand la variable de dépôt
   `NUGET_USER` existe ; sinon le secret `NUGET_API_KEY` ; sinon aucun envoi,
   un avertissement, et la Release se crée quand même.
4. **Republier** : lancé à la main sur `main` avec l'option `republish`, le
   workflow reprend le tag de la version courante, ré-empaquette et pousse sur
   nuget.org (pour une version publiée avant la configuration de NuGet).
5. Le choix du numéro suit toujours SemVer (ADR 0019, ADR 0114) ;
   `build/tools/ship-public-api.sh` verse `PublicAPI.Unshipped.txt` dans
   `Shipped` lors d'une mineure ou d'une majeure, dans la même PR que la
   montée de version.

## Conséquences

- Une PR qui ne change pas la version ne publie rien ; une PR qui la change
  publie une seule fois, après une vérification verte de `main`.
- Les identifiants `HackerOs.*` deviennent publics sur nuget.org ; le flux
  local reste disponible pour essayer une version non publiée.
- Le workflow n'est jamais déclenché par une PR ni par une branche
  `*/base`.

## Options écartées

- **Version calculée à partir des tags** (MinVer, GitVersion) : la version ne
  serait plus dans un fichier, et un tag manuel deviendrait l'action de
  publication.
- **Déclencher sur le changement de `Versions.props` dans le push** : une
  vérification annulée par un push suivant ferait perdre la publication ; le
  test « tag absent » reprend au commit suivant.
- **Publier depuis le workflow de vérification** : son groupe de concurrence
  annule les exécutions en cours, ce qui pourrait couper un envoi NuGet.

## Usage

Voir [`releases.md`](../releases.md).
