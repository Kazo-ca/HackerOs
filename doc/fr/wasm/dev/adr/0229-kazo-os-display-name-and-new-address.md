> [English version](../../../../en/wasm/dev/adr/0229-kazo-os-display-name-and-new-address.md)

# ADR 0229 : « Kazo OS » avec une espace, dépôts `Kazo.Os.App.*`, redirection vers os.kazo.ca

## Statut

Accepté le 2026-11-04 (demandé par Benoit, série `rename/base`, branche
`rename/2b-display-space`).

- **Complète :** [ADR 0228](0228-rename-to-kazoos.md).
- **Remplace :** le nom affiché « KazoOS » de l'ADR 0228 (contexte, point 1,
  et §1) et le chantier 7 de l'ADR 0228 §3 (avis « KazoOS a déménagé » et
  restauration d'une sauvegarde).

## Contexte

Après l'ADR 0228, Benoit (2026-11-04) :

1. Sur l'image sociale : « Il manque un espace entre Kazo et Os dans
   l'image », puis il choisit **« Kazo OS »** avec une espace.
2. Noms des dépôts d'apps : il choisit **`Kazo.Os.App.*`**, comme les
   espaces de noms.
3. Adresse du site : « on laisse faire la migration. Mais on doit faire une
   redirection ». La redirection est faite par Cloudflare ; « je fais la
   redirection Cloudflare dans une autre session ».

## Décision

1. **Nom affiché : « Kazo OS ».** Titre, PWA, écran de connexion,
   notifications, descriptions des apps, readmes et docs actuelles disent
   « Kazo OS ». Les noms techniques ne changent pas : `Kazo.Os.*`, `Kazo*`,
   `kazoos` (bases, chemins, extension `.kazoos`).
2. **Dépôts.** Le dépôt principal est `Kazo-ca/Os`. Les dépôts d'apps
   suivent les espaces de noms : `HackerOs-App-Chess` → `Kazo.Os.App.Chess`
   (de même Webamp, IRC, EmulatorJS, BoxedWine), `HackerOs-AppTemplate` →
   `Kazo.Os.AppTemplate`, `HackerOs-AppMarketPlace` →
   `Kazo.Os.AppMarketPlace`. Benoit renomme les dépôts ; le chantier 8 met
   leur code et les liens à jour.
3. **Adresse du site.** `os.kazo.ca` est la seule adresse servie par GitHub
   Pages. `hackos.kazo.ca` est redirigé par un Worker Cloudflare : il répond
   à `/service-worker.js` par un script qui vide le cache, se désinscrit et
   envoie les onglets vers `os.kazo.ca`, et redirige tout le reste avec le
   même chemin (PR #362). **Aucune récupération des données** : les comptes
   et fichiers gardés par le navigateur sur `hackos.kazo.ca` ne sont plus
   accessibles après la bascule (une autre adresse est une autre origine
   pour le navigateur).
4. Le chantier 7 se limite donc au `CNAME` de la publication GitHub Pages,
   aux liens et aux docs qui visent encore `hackos.kazo.ca`.
5. La migration du chantier 6 reste : elle sert à tout hôte qui garde son
   adresse (serveur installable, déploiement personnel).

## Conséquences

- Les utilisateurs de `hackos.kazo.ca` repartent d'une machine vide sur
  `os.kazo.ca`. Ceux qui veulent garder leurs données font une sauvegarde
  avant la bascule.
- Les ADR 0228 et 0229 se lisent ensemble : la correspondance des noms
  techniques reste celle de l'ADR 0228.
