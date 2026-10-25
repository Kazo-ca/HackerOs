# Accueil et tiroir d’applications mobiles

## Objet

L’accueil Android/iOS présente des raccourcis sur plusieurs pages et un dock.
Toutes les apps mobiles compatibles restent accessibles dans le tiroir, même
sans raccourci. L’[ADR 0165](adr/0165-mobile-home-drawer-and-mouse-controls.md)
complète le [plan mobile archivé](old/mobile-interface-platform-plan.md).
L'[ADR 0166](adr/0166-mobile-layout-conf-on-desktop.md) remplace son chemin et
son format de persistance par un fichier texte sous le bureau.

## Utilisation

- Revenir au desktop : toucher/cliquer la poignée du haut ou glisser vers le bas,
  puis choisir **Desktop** dans le volet. Le simple clic fonctionne aussi à la souris.
- Ouvrir le tiroir : glisser vers le haut depuis Accueil (cercle Android, poignée
  iOS) ou le dock ; ou utiliser **Toutes les applications** dans le dock.
- Rechercher puis toucher une app la lance. **+** ajoute un raccourci à la page
  courante ; l’étoile ajoute/retire du dock. Une app déjà ouverte est restaurée.
- Le tiroir place le focus dans la recherche, garde la tabulation dans ses
  commandes et restitue le focus à la fermeture ; Échap ferme le tiroir.
- Accueil masque l’app sans l’arrêter et ferme les panneaux. Retour ferme d’abord
  le tiroir, le volet ou les récents avant d’interroger l’app. Récents garde ses commandes.
- Glisser latéralement change de page. Les points, les flèches et les touches
  gauche/droite offrent des alternatives. Les points se révèlent après navigation.
- **Modifier**, un appui long ou un clic droit sur une icône entre en édition.
  Le glisser-déposer souris et les flèches réordonnent les raccourcis. Les flèches
  de transfert déplacent vers une autre page ; la page suivante est créée si besoin.
  Une page vide peut être supprimée. Retirer un raccourci ne désinstalle pas l’app.
- **Paramètres > Apparence > Commandes souris du mode mobile** masque ou révèle
  les flèches de page et indications de geste. Les gestes restent utilisables à la souris.

## Architecture

| Élément | Responsabilité |
| --- | --- |
| `MobileShell` | Compose accueil et app courante, conserve l’état du tiroir pour Retour/Accueil, suit la taille du workspace. |
| `MobileHomeScreen` et ses fichiers collocatés | Catalogue autorisé, pages, recherche, édition et dock. Paramètres `IsDrawerOpen`, `OnDrawerChanged`, `OnAppSelected`. |
| `MobileAppIcon` | Image déclarée de l’app, partagée entre pages, dock et tiroir ; repli Lucide. |
| `MobileHomeLayout` | Modèle interne borné, lecture avec `ConfigDocumentFormat`, écriture texte déterministe et ancienne lecture JSON pour migration seulement. |
| `MobileHomeLayoutStore` | Fichier VFS caché via `IOsSettingsService`, éditions sérialisées et projection publiée après succès. |
| `MobileSettingsDocuments` | Schéma du document OS `mobile`, accessible à Paramètres par `HackSettings.OpenSystemAsync`. |
| `WindowProcessObserver` | Pont processus/fenêtres partagé avec le desktop pour les lancements indirects et arrêts. |
| `wwwroot/mobileGestures.js` | Primitives navigateur : capture, appui long, clic parasite, taille ; C# décide du sens et des seuils. |

Les apps proposées sont de genre `Window`, visibles, activées et compatibles
`mobile` selon leur manifeste. Les noms suivent la langue de session. Tous les
textes sont disponibles en français et anglais. Les fichiers restent locaux au
système simulé, sans dépendre du backend optionnel.

Les quatorze apps système fenêtrées déclarent un `MobileEntryPoint` partagé
avec leur composant desktop ; le générateur émet la forme `platform` avec les
deux plateformes. Le filtre du tiroir et l’orchestrateur conservent les contrôles
de compatibilité pour les autres apps.

## Persistance et paramètres

`~/Desktop/.mobile/layout.conf` :

```ini
# HackerOS mobile Home layout
version=1

[dock]
apps=org.hackeros.browser,org.hackeros.settings

[pages]
1=org.hackeros.text-editor
2=org.hackeros.calculator
```

Les pages sont numérotées de 1 à N sans trou ; une valeur vide conserve une
page vide. L'ordre des IDs séparés par des virgules définit les positions.
Commentaires `#`, sections et clés suivent la grammaire des paramètres de l'OS.
Limites : 24 pages, 20 raccourcis
par page, 5 au dock. Il reste toujours une page ; les pages vides sont permises.
Les doublons, IDs invalides, versions inconnues et fichiers trop grands sont refusés.
Les IDs indisponibles restent enregistrés et sont masqués temporairement au rendu.
Un fichier invalide est conservé jusqu’à une action d’édition explicite.

Le répertoire `.mobile` se trouve dans le bureau de chaque utilisateur.
Le bureau masque les noms commençant par un point.
L’explorateur masque par défaut les noms commençant par un point. Son bouton
**Afficher les fichiers et dossiers cachés** (œil) active/désactive leur affichage
dans la fenêtre courante, avec le même filtre que la recherche et sans écriture.
La disposition fait partie des fichiers du bureau de l'utilisateur.
Chaque édition remplace un seul fichier ; changer de page n’écrit rien. Aucun
localStorage ni changement de schéma IndexedDB.

Si le nouveau fichier n'existe pas, une ancienne disposition valide de
`~/.config/hackeros/.mobile/layout.json` est convertie automatiquement au chargement.
L'ancien fichier reste intact ; aucune écriture JSON n'est effectuée. Un
`layout.conf` existant est toujours prioritaire, même invalide, et n'est jamais
écrasé par cette migration. Sans ancien fichier valide, la première édition
crée la configuration. Les écritures suivantes normalisent son texte.

`OsSettingsLocation.UserHome` permet à `OsSettingsFile` de résoudre le chemin
relatif `Desktop/.mobile/layout.conf` avec les droits de la session.

`/etc/hackeros/mobile.config`, surchargé par `~/.config/hackeros/mobile.config` :

```ini
mouseHelpers=true
```

Le verrouillage administrateur, l’origine des valeurs et les permissions suivent
le moteur de paramètres à couches. La désactivation masque les aides, jamais les gestes.

## Vérification

- `MobileHomeLayoutTests` : chemin caché, ordre, IDs indisponibles, données invalides
  laissées intactes, limites/doublons, éditions sérialisées et absence de session,
  commentaires/espaces, migration unique du JSON et priorité d'un conf existant.
- Tests de navigation mobile existants : historique, Retour et fermeture des panneaux.
- `BuildKnownLazyAppsTests` : le catalogue agrégé conserve toutes les assemblies
  desktop lorsque les apps adoptent `platform.entryPoints` ; la calculatrice
  déclare la même assembly pour desktop et mobile.
- `MobileHomeScreenTests` (UI.E2E) : clic du haut, retour desktop, tiroir par geste
  depuis Accueil, ajout/dock, édition, pagination, option souris en direct et
  persistance après rechargement, Android/souris et iOS/toucher émulés dans Chromium ;
  `.mobile` masqué/révélé par la commande de l'explorateur.
- `FileViewTreeTests` : le filtre des fichiers cachés suit aussi les descendants
  déjà chargés et l'ordre de navigation clavier.
- Régressions : changement de shell, volet, récents et navigation clavier du desktop.

## Liste des tâches

- [x] Corriger la poignée supérieure et le clic parasite après un glissement.
- [x] Ajouter accueil, dock, tiroir et restauration des apps déjà ouvertes.
- [x] Enregistrer les pages et le dock dans la configuration VFS cachée.
- [x] Ajouter édition, pagination et gestes toucher/souris.
- [x] Ajouter les aides souris et leur option dans les paramètres à couches.
- [x] Ajouter la commande des fichiers cachés de l'explorateur et filtrer l'arbre.
- [x] Ajouter les tests et la documentation.
- [x] Terminer la validation navigateur Android/iOS et les régressions du shell.
- [x] Déplacer la disposition vers `~/Desktop/.mobile/layout.conf` et migrer l'ancien JSON (ADR 0166).

Validation initiale du 2026-10-23 (ADR 0165) : 53 tests ciblés Platform Blazor (disposition,
navigation, lancement, géométrie), 20 Paramètres, 94 contrôle de fichiers et
30 Explorateur réussis. Les sept scénarios navigateur couvrent Android/souris,
iOS/tactile, changement de shell, choix mobile après rechargement, volet par
glissement, récents/Retour et raccourcis clavier desktop. Le parcours complet
vérifie aussi le masquage de `.mobile`. Le build Release du PWA Ecosystem est
vérifié sans backend.

Validation de l'ADR 0166 : les 20 tests de disposition/migration et les deux
parcours navigateur Android/souris et iOS/tactile passent avec le fichier conf.
Ils vérifient sa persistance après rechargement, le dossier absent des icônes
du bureau, sa visibilité explicite dans l'explorateur et la présence de
`layout.conf`. Compilation Release réussie sans avertissement.
