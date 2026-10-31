> [English version](../../../../en/wasm/dev/plans/hackeros-store.md)

# Plan : le Hack Store

Plan **validé le 2026-10-29** par Benoit, dans le fil « Planifier le Hack
Store ». Aucun chantier n'est lancé : ce document liste les choses à faire,
dans l'ordre. Il remplace la note du 2026-10-28 (« HackerOs Store »), qui
annonçait ce plan à part pour le magasin d'apps de
[`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md) (section 9) et de
[`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md) (question 1).

## But

Une app système **Hack Store**, façon Play Store ou Microsoft Store, épinglée
par défaut dans le menu Démarrer, qui liste les apps qu'on peut installer dans
HackerOS, montre leur fiche, et les installe, met à jour ou retire en un clic.
Les apps viennent d'un catalogue relu (`Kazo-ca/HackerOs-AppMarketPlace`), où
chaque version est compilée par nous depuis son code source. En v2, le Store
propose aussi des packs de thèmes.

## Décisions

| ADR | Sujet |
| --- | --- |
| [0188](../adr/0188-hack-store-and-app-catalog.md) | L'app Hack Store (v1), son épinglage par défaut, les mises à jour, le format du dépôt catalogue (un dossier par version), les actions GitHub de vérification et de publication, l'action des créateurs d'apps |
| [0189](../adr/0189-app-sources-trust-and-per-user-install.md) | Sources de confiance (compilées avec l'OS ou ajoutées par un administrateur), installation pour soi sans administrateur, liste noire, validation en deux étapes de l'installation système ; révise l'ADR 0182 Q4 |
| [0190](../adr/0190-theme-packs.md) | v2 : packs `.hacktheme` (thèmes, fonds d'écran, icônes, sons, curseurs) et les systèmes de l'OS à créer avant |

Déjà en place : [ADR 0176](../adr/0176-app-repository-template.md) (dépôt
template d'apps) et [ADR 0182](../adr/0182-install-apps-from-github.md)
(paquets `.hackapp`, `hackpkg`).

## Choses à faire, dans l'ordre

Chaque ligne est un chantier, avec sa branche et sa PR.

1. **Modèle `hackeros-solution`** : tâches de l'ADR 0176, plus le workflow de
   publication vers le catalogue (ADR 0188 §7). Les apps du catalogue partent
   de ce modèle. **Fait** (branche `todo/1-hackstore-solution-template`,
   [ADR 0207](../adr/0207-solution-template-implementation.md),
   [`app-repository.md`](../../framework/app-repository.md)) ; le dépôt
   template est régénéré à la release suivante.
2. **Ressources `wwwroot/` des apps installées** (fait,
   [ADR 0206](../adr/0206-serve-installed-app-assets.md), issue #247) : servies à
   `_content/<assemblage>/` par le service worker depuis un cache rempli au
   premier lancement de l'app, Web Workers compris. Nécessaire pour toute app
   qui enveloppe un projet JavaScript.
3. **Confiance et installation pour soi** (ADR 0189) : `app-sources.config`
   (`[catalogs]`, `[updates]`, valeurs fixées à la compilation, sources
   ajoutées par un administrateur), `app-policy.config`, installation dans
   `~/.local/share/hackeros/apps`, catalogue construit à l'ouverture de la
   session, fenêtre de validation en deux étapes, `hackpkg --user/--system`,
   `sources`, `policy`, Paramètres ▸ Apps ▸ Sources. **Fait** (issue #248,
   [ADR 0208](../adr/0208-hack-store-sources-trust-build.md) : catalogue chargé
   au démarrage et masqué par session).
4. **Dépôt `HackerOs-AppMarketPlace`** (ADR 0188 §4 à §6) : `categories.json`,
   modèle de PR, `validate.yml` (format, manifeste, commit, compilation,
   vérification des API, CodeQL, commentaire des capacités), `publish.yml`,
   branche `packages`, `catalog.json`, 3 versions gardées.
5. **App Hack Store v1** (ADR 0188 §1 à §3) : accueil par catégories,
   recherche, fiches, Installer / Ouvrir / Mettre à jour / Désinstaller,
   onglet Installées, « Redémarrer pour terminer », vérification des mises à
   jour (démarrage puis toutes les 6 heures), badge et notification,
   épinglage par défaut et migration des profils existants, bureau et mobile.
   `hackpkg install <appId>` depuis les catalogues. **Fait** (branche
   `todo/5-hackstore-app`, issue #249,
   [ADR 0212](../adr/0212-hack-store-app-build.md)).
6. **Première app du catalogue** : une app tierce, dans son propre dépôt et
   faite avec le modèle, qui teste toute la chaîne (dépôt de l'app → PR au
   catalogue → vérification → publication → installation par le Store).
7. **Plus tard** : ajout d'une app au catalogue sans redémarrage (ADR 0182,
   Q5 B) ; signature des paquets ; installation transactionnelle et migration
   des données d'une version à l'autre (question 1 de
   `wasm-v3-completion-plan.md`).

### v2 : packs de thèmes (ADR 0190)

8. Format `.hacktheme`, vérifications, installation, onglet Thèmes du Store
   (packs de thèmes `.theme` seulement au début).
9. Un chantier par nouveau système de l'OS, chacun avec son ADR : fonds
   d'écran en image, packs d'icônes, sons des évènements, thèmes de curseurs.
   Le Store accepte chaque partie d'un pack quand son système existe.

## Règles

- Ce dépôt ne contient le code d'aucune app du catalogue : chaque app vit dans
  son propre dépôt. **Aucun jeu sur ce repository**
  ([ADR 0135](../adr/0135-no-game-in-this-repository.md)).
- Pas de fusion automatique vers `main` ; Benoit décide.
- Chaque chantier met à jour les index de la documentation (fr et en).
