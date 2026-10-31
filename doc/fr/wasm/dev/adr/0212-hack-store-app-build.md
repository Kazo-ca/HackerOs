> [English version](../../../../en/wasm/dev/adr/0212-hack-store-app-build.md)

# ADR 0212 : l'app Hack Store v1, ajustements de mise en œuvre

## Statut

Accepté le 2026-10-31, chantier `todo/5-hackstore-app` (issue `TODO`
[#249](https://github.com/Kazo-ca/HackerOs/issues/249), Hack Store 5). Met en
œuvre l'[ADR 0188](0188-hack-store-and-app-catalog.md) §1 à §3 et §8, sur les
sources et la confiance de l'[ADR 0208](0208-hack-store-sources-trust-build.md).
Ne change aucune décision de l'ADR 0188 ; fixe ce qu'elle laissait ouvert.
Quatre questions ont été posées à Benoit (Q1 à Q4 ci-dessous) ; les
recommandations sont appliquées en attendant ses réponses.

## Contexte

L'ADR 0188 décide l'app Hack Store, son épinglage par défaut, ses mises à jour
et la lecture des catalogues par `hackpkg`. Le format de `catalog.json` est
celui de la branche `catalog/1-initial-catalog` de
`Kazo-ca/HackerOs-AppMarketPlace` (`schemaVersion` 1, chemins relatifs à la
racine de la branche `packages`, à côté de `catalog.json`). Il restait à
fixer : comment une installation depuis un catalogue est écrite dans
`[installed]`, qui peut « Redémarrer pour terminer », où se montre le badge,
quel catalogue gagne quand plusieurs listent la même app, et comment marquer
qu'un profil a déjà reçu l'épinglage par défaut.

## Décision

1. **Source d'une app de catalogue (Q2 A).** Une app installée depuis un
   catalogue est écrite `catalog:<nom>/<appId>@<version>` dans `[installed]`
   (par exemple `catalog:official/org.example.notes@1.2.0`), à côté de la forme
   `github:owner/repo@version` de l'ADR 0182. Sa confiance est celle de la clé
   `catalog:<nom>` de `[trusted]` ; le catalogue officiel est **Vérifié**
   (ADR 0208 §1). La mise à jour relit le même catalogue. L'installeur
   transforme l'entrée du catalogue en index de paquet (version, chemin,
   SHA-256), si bien que l'installation, les vérifications (SHA-256, API,
   SDK) et la mise à jour gardent un seul chemin de code.
2. **Lecture des catalogues.** L'OS télécharge lui-même `catalog.json`
   (8 Mio au plus) et le garde 5 minutes ; le bouton Actualiser du Store le
   relit. Un catalogue illisible est montré avec son erreur sans bloquer les
   autres ; une app ou une version invalide (identifiant, chemin qui sort du
   catalogue, SHA-256, taille) est ignorée avec un avertissement dans
   `/var/log/syslog`. `kind` autre que `app` est ignoré jusqu'aux packs de
   thèmes (ADR 0190). La description `description.<langue>.md` (64 Kio au plus)
   est lue dans la langue de la session, puis la langue sans région, puis
   l'anglais. Le Store la dessine lui-même (titres, paragraphes, listes, code,
   gras, italique, liens `https` seulement) : aucun HTML n'est produit depuis
   le texte d'un auteur.
3. **« Redémarrer pour terminer » sans administrateur (Q1 A).** Le bouton
   redémarre HackerOS comme Démarrer ▸ Redémarrer, sans authentification, et
   seulement quand une app installée, mise à jour ou retirée attend le
   redémarrage (sinon `apps.package.nothing-pending`).
4. **Un appId seul (Q4 A).** `hackpkg install <appId>[@version]`, `hackpkg
   info <appId>` et le Store cherchent l'app dans le catalogue officiel
   d'abord, puis dans les autres catalogues par ordre de nom ; le premier qui
   la liste gagne (`apps.package.not-in-catalog` sinon). Une app déjà
   installée garde la fiche du catalogue d'où elle vient.
5. **Vérification des mises à jour et badge (Q3 A).** Un service de session
   `AppUpdateMonitor` vérifie à l'ouverture de la session puis toutes les
   `[updates] interval-hours` heures (6 par défaut). Il compte les mises à jour
   des apps du système et des apps personnelles de l'utilisateur, met ce
   nombre en badge sur l'icône du Hack Store dans le menu Démarrer, sur
   l'écran d'accueil mobile et sur son bouton de la barre des tâches, et
   envoie une notification (une seule par ensemble de mises à jour) dont
   l'action ouvre l'onglet Installées (`--page=installed`). Il s'arrête et
   efface le badge à la fermeture de session. Pas de mise à jour automatique.
6. **Épinglage par défaut.** `start-menu.json` reçoit, par profil, une liste
   facultative `defaultPinsApplied` des apps déjà épinglées par défaut. Au
   premier chargement d'un profil (nouveau ou existant), chaque app de la liste
   par défaut (`org.hackeros.store`) absente de `defaultPinsApplied` est ajoutée
   après les épingles de l'utilisateur et notée ; une app désépinglée ensuite
   ne revient pas.
7. **SDK.** `HackAppPackages.GetCatalogsAsync`, `GetCatalogDescriptionAsync`,
   `GetUpdatesAsync`, `RestartToFinishAsync` ; `HackAppCatalog`,
   `HackCatalogApp`, `HackCatalogAppVersion`, `HackCatalogCategory`,
   `HackPackageUpdate`. Les nouveaux membres de `IAppManagementGateway` ont une
   implémentation par défaut qui lève `NotSupportedException`. `InstallAsync`
   et `GetInfoAsync` acceptent `catalog:<nom>/<appId>` et un appId seul.
8. **L'app.** `HackerOs.Apps.Store`, app système (`IsSystemApp="true"`, non
   désinstallable), capacités `apps.manage` et `apps.launch`, une instance par
   utilisateur, même écran sur le bureau et le mobile (requête de conteneur).

## Conséquences

- Le dépôt catalogue doit garder ses chemins relatifs à `catalog.json` ; un
  changement de `schemaVersion` est refusé par les OS déjà publiés
  (`apps.catalog.unsupported`).
- Une app installée par le Store ou par `hackpkg install <appId>` apparaît
  toujours après un redémarrage (ADR 0182 Q5 C) ; l'ajout à chaud reste
  l'issue #251.
- Le modèle d'hôte (`HackerOs.Templates`) référence `HackerOs.Apps.Store`, qui
  est publié avec la version suivante.

## Questions posées à Benoit

| Question | Choix | Recommandation appliquée |
|---|---|---|
| Q1 « Redémarrer pour terminer » | A sans administrateur ; B avec administrateur | A |
| Q2 Source écrite | A `catalog:<nom>/<appId>` ; B le dépôt GitHub de l'app | A |
| Q3 Badge | A menu Démarrer et barre des tâches ; B menu Démarrer seulement | A |
| Q4 Plusieurs catalogues | A officiel puis par nom ; B demander | A |

## Tâches

- [x] Source `catalog:`, lecture de `catalog.json`, installation et mise à jour depuis un catalogue.
- [x] `hackpkg install <appId>` et `hackpkg info <appId>`.
- [x] SDK : catalogues, descriptions, mises à jour, redémarrage.
- [x] `AppUpdateMonitor`, badge (menu Démarrer, mobile, barre des tâches), notification.
- [x] Épinglage par défaut et migration des profils (`defaultPinsApplied`).
- [x] App Hack Store : accueil, recherche, fiche, Installées, Redémarrer pour terminer, bureau et mobile.
- [x] Tests et documentation (`app-installation.md`, `linux-commands.md`).
