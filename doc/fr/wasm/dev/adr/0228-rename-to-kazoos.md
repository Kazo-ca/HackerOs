> [English version](../../../../en/wasm/dev/adr/0228-rename-to-kazoos.md)

# ADR 0228 : HackerOS devient KazoOS

## Statut

Accepté le 2026-11-04 (demandé par Benoit, série `rename/base`, branche
`rename/1-adr-kazoos`).

- **Touche :** le nom de toutes les ADR, docs, projets, paquets et types qui
  portent « HackerOS », « HackerOs » ou le préfixe `Hack`. Les ADR acceptées
  ne sont pas réécrites : elles gardent l'ancien nom, cette ADR donne la
  correspondance.

## Contexte

Benoit (2026-11-04) : « j'aimerais trouver un nouveau nom ... Peut-être
KazoOs ? HackerOs est déjà pris je crois ».

Recherche faite le même jour
(`/mnt/project-files/rename/nom-du-projet.md` dans le projet) :

- **HackerOS est pris** par une distribution Linux active basée sur Debian
  (organisation GitHub `HackerOS-Linux-System`, version 4.x, ISO sur
  SourceForge, articles de presse). Elle passe devant ce dépôt dans la
  recherche GitHub ; `hackeros.com` est à vendre chez un revendeur.
- **KazoOS est libre** : aucun produit ni dépôt de ce nom, aucun paquet NuGet
  `Kazo.*`. Les domaines `kazoos.com/.ca/.org` sont pris (le mot « kazoo »),
  `kazoos.dev` et `kazoos.app` semblaient libres.
- « Kazo WebOS » a été écarté : webOS est une marque déposée de LG.

Réponses de Benoit :

1. Nom affiché : **KazoOS**.
2. Ampleur : **tout renommer**, y compris le préfixe `Hack` des types du SDK,
   remplacé par **`Kazo`** (« 3, prendre juste Kazo pour remplacer les
   préfix `Hack` »).
3. Espaces de noms et paquets : **`Kazo.Os.*`**.
4. Il renomme lui-même le dépôt `Kazo-ca/HackerOs` en **`Kazo-ca/Os`**.
5. Adresse du site : **les deux, avec redirection** : `os.kazo.ca` devient
   l'adresse principale, `hackos.kazo.ca` reste ouvert pour récupérer les
   données.

## Décision

### 1. Correspondance des noms

| Avant | Après |
|---|---|
| HackerOS (nom affiché, titre, PWA, docs) | KazoOS |
| `HackerOs.*` (projets, assemblages, espaces de noms, paquets NuGet) | `Kazo.Os.*` (ex. `Kazo.Os.AppSdk`, `Kazo.Os.Platform.Core`) |
| `HackerOs.sln`, dossier `wasm2/HackerOs/` | `Kazo.Os.sln`, dossier `wasm2/KazoOs/` |
| Types `Hack*` (`HackFile`, `HackPath`, `HackExecutionContext`...) | `Kazo*` (`KazoFile`, `KazoPath`, `KazoExecutionContext`...) |
| Types `HackerOs*` / `Hacker*` (`HackerOsVersion`, `HackerIcon`...) | `KazoOs*` / `Kazo*` (`KazoOsVersion`, `KazoIcon`...) |
| Apps `HackPaint`, ... | `KazoPaint`, ... |
| Commandes `hackpkg`, `hackweb`, `hackwebd` | `kazopkg`, `kazoweb`, `kazowebd` |
| ASP.Hack | ASP.Kazo |
| Hack Store | Kazo Store |
| Extensions `.hackapp`, `.hacktheme`, `.hackeros` | `.kazoapp`, `.kazotheme`, `.kazoos` |
| Fichiers `/etc/hackeros`, `~/.config/hackeros`, `/etc/hackweb`, `/var/log/hackweb` | `/etc/kazoos`, `~/.config/kazoos`, `/etc/kazoweb`, `/var/log/kazoweb` |
| Bases IndexedDB `hackeros`, `hackeros-*`, `hackweb.*` | `kazoos`, `kazoos-*`, `kazoweb.*` |
| Dépôts `HackerOs-App-*`, `HackerOs-AppTemplate`, `HackerOs-AppMarketPlace` | renommés par Benoit ; leur code suit les paquets `Kazo.Os.*` |

La phrase « Aucun jeu sur ce repository » (ADR 0135) ne change pas.

### 2. Compatibilité

- **SDK 3.0.0.** Le renommage casse toute l'API publique : les paquets
  `Kazo.Os.*` commencent en 3.0.0. Les 43 paquets `HackerOs.*` restent sur
  nuget.org, marqués obsolètes avec un renvoi vers leur remplaçant ; aucun
  paquet de transition n'est publié. Le préfixe `Kazo.Os.` est réservé sur
  nuget.org (réservation de préfixe d'identifiant).
- **Données des utilisateurs.** Au premier démarrage de KazoOS, une
  migration unique copie chaque base `hackeros*` / `hackweb.*` vers son
  nouveau nom, déplace les fichiers de configuration du système de fichiers
  virtuel vers leurs nouveaux chemins, renomme les clés `localStorage`, puis
  supprime les anciennes. Elle est rejouable : interrompue, elle reprend au
  démarrage suivant. Un test couvre une base au format actuel
  (`Tests/fixtures/indexeddb`).
- **Fichiers existants.** Les anciennes extensions (`.hackapp`,
  `.hacktheme`, `.hackeros`) restent lues et ouvertes ; KazoOS écrit
  seulement les nouvelles. Les anciennes commandes (`hackpkg`...) restent
  un alias qui écrit un avertissement, pendant une version mineure.
- **Docs.** Les docs actuelles disent KazoOS. Les ADR acceptées et les docs
  sous `old/` gardent leur texte ; leurs liens vers des fichiers renommés
  sont corrigés.

### 3. Chantiers de la série `rename/base`

Chaque chantier a sa branche `rename/<n>-<sujet>` et sa PR vers
`rename/base`. Les chantiers 3 et 4 touchent presque tous les fichiers : ils
passent l'un après l'autre, pas en parallèle.

1. `rename/1-adr-kazoos` : cette ADR.
2. `rename/2-display-name` : nom affiché (titre, `manifest.webmanifest`,
   écran de connexion, À propos), readmes, `AGENTS.md`, `CLAUDE.md`,
   `features.md`, texte des docs actuelles.
3. `rename/3-namespaces` : dossiers, `.csproj`, `.sln`, espaces de noms,
   identifiants de paquets, scripts de build, workflows GitHub Actions,
   modèles `dotnet new`.
4. `rename/4-type-prefix` : préfixe `Hack` → `Kazo` des types, fichiers
   `PublicAPI.*.txt`, montage de compatibilité `Tests/fixtures/sdk-compat`,
   version 3.0.0.
5. `rename/5-system-names` : commandes, extensions, chemins du système de
   fichiers, ASP.Kazo, Kazo Store, avec la lecture des anciens noms.
6. `rename/6-storage-migration` : migration des bases IndexedDB, des
   fichiers de configuration et du `localStorage`, avec ses tests.
7. `rename/7-new-address` : passage à `os.kazo.ca`. Le navigateur voit
   deux sites différents (deux origines), donc les bases IndexedDB de
   `hackos.kazo.ca` ne sont pas visibles depuis `os.kazo.ca`.
   `hackos.kazo.ca` continue de servir l'OS : un visiteur sans compte y est
   redirigé vers `os.kazo.ca` ; un visiteur qui a des données voit un avis
   « KazoOS a déménagé » qui propose de faire une sauvegarde (Paramètres ▸
   Sauvegarde) puis ouvre `os.kazo.ca`, où il la restaure. GitHub Pages ne
   sert qu'un domaine par dépôt : `hackos.kazo.ca` passe sur un second site
   Pages (dépôt séparé) ou reste le domaine de ce dépôt avec `os.kazo.ca`
   en second ; le chantier choisit et documente.
8. Après la publication de 3.0.0 : mise à jour des dépôts satellites
   (modèle d'app, marché d'apps, apps portées) vers `Kazo.Os.*`.

## Conséquences

- Toutes les apps tierces doivent passer à `Kazo.Os.*` 3.0.0 et changer
  leurs `using` et noms de types. Le guide de migration va dans
  `doc/{fr,en}/wasm/framework/`.
- Sur `hackos.kazo.ca`, les utilisateurs existants gardent leurs données
  grâce à la migration du point 2. Pour les emporter sur `os.kazo.ca`, ils
  passent par une sauvegarde (chantier 7) : rien n'est transféré sans eux.
- GitHub redirige l'ancien nom du dépôt vers `Kazo-ca/Os`, mais les liens
  des docs, des gabarits d'issues et des workflows sont mis à jour.
- Le chantier 3 change presque tous les chemins de fichiers : une PR
  ouverte avant lui devra être rebasée.
