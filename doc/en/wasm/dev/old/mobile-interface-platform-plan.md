# Plan de plateforme et d’interface Mobile

> Mise à jour du 2026-10-23 : accueil paginé, dock, tiroir et aides souris sont
> décrits dans [mobile-home-screen.md](../mobile-home-screen.md) et
> l'[ADR 0165](../adr/0165-mobile-home-drawer-and-mouse-controls.md), qui
> complètent les primitives de navigation de ce plan archivé.

**Statut :** Phase 0 (fondation) faite le 2026-08-18, Phase 1 (manifeste et
résolution de point d’entrée) faite le 2026-08-18, Phase 2a (primitives shell
Mobile), Phase 2b (`MOB-008`, changement de shell contrôlé), Phase 2c
(`MOB-012`/`MOB-013`, contrat Back applicatif) et `MOB-011` (pile de
navigation et surface Recent, complétant les étapes 1/3/4/5 de §7.3 — Phase 2
est donc entièrement close) faites entre le 2026-08-18 et le 2026-08-23, puis
`MOB-015` (clavier virtuel réutilisable, généralisé au-delà du seul Terminal —
voir §16.11) faite le 2026-08-23 — voir §16 ; le reste (`MOB-016` à `MOB-018`)
reste à l’état de proposition  
**Modes initiaux :** `desktop`, `mobile`  
**Extensibilité requise :** ajout ultérieur d’autres plateformes sans refonte du
manifeste ou du catalogue

## 0. Note de mise à jour (2026-08-18)

Ce document a été écrit le 2026-08-03, **avant** l’extraction du moteur
fenêtres/taskbar en packages autonomes (`HackerOs.Windowing.Core`,
`HackerOs.Windowing.Abstractions`, `HackerOs.Windowing.Blazor`,
`HackerOs.Taskbar.Blazor` — finalisée le 2026-08-17, voir
[`window-taskbar-export-plan.md`](window-taskbar-export-plan.md) et
[`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md)). Le texte
original ci-dessous ne nomme jamais ces packages ; §16 fait le lien entre les
concepts qu’il décrit et leur emplacement réel dans le code, et resséquence
`MOB-002` à `MOB-018` en phases à la lumière de ce qui existe maintenant.

Une première tranche (« Phase 0 ») a été implémentée le 2026-08-18 : elle pose
`AppPlatformId`, la préférence `UiPlatformPreference` (persistance seule, pas
encore de changement de shell), et — anticipant le besoin d’un point d’entrée
UI pour ce toggle — une extensibilité `RenderFragment` sur le taskbar
(`ITaskbarClockPanelSource`) permettant d’ouvrir un panneau
notifications/calendrier/toggle en cliquant sur l’horloge, avec le contenu du
panneau conçu entièrement en dehors du package taskbar. Détail complet en §16.

## 1. Objectif

Ajouter un mode Mobile complet à HackerOS tout en conservant le mode Desktop.
Une application pourra supporter une ou plusieurs plateformes et utiliser soit un
point d’entrée partagé, soit un point d’entrée distinct par plateforme, dans un
manifeste unique et avec un seul `AppId`.

Le mode Mobile doit :

- afficher une seule application plein écran à la fois ;
- remplacer la taskbar Desktop par une barre de navigation inspirée d’Android
  comportant Triangle/Back, Cercle/Home et Carré/Recent ;
- offrir une pile de navigation cohérente ;
- fournir au Terminal un clavier virtuel HackerOS sans ouvrir le clavier natif du
  téléphone ;
- filtrer les applications incompatibles avec la plateforme active ;
- permettre de passer explicitement entre Desktop et Mobile ;
- mémoriser le choix par appareil ;
- utiliser la détection du navigateur tant qu’aucun choix explicite n’a été
  enregistré.

Le bouton Back dans la barre de menu propre à l’application est une fonctionnalité
Desktop seulement. Sur Mobile, la navigation Back est fournie par le triangle de
la barre système.

## 2. Vocabulaire

- **Plateforme :** environnement de présentation logique tel que `desktop` ou
  `mobile`. Ce n’est pas le système d’exploitation réel du téléphone.
- **Mode actif :** plateforme actuellement sélectionnée par le shell HackerOS.
- **Détection automatique :** plateforme suggérée par les capacités rapportées par
  le navigateur lorsqu’aucune préférence explicite n’existe.
- **Point d’entrée de plateforme :** assembly et type utilisés pour lancer une
  application sur une ou plusieurs plateformes.
- **Back applicatif :** demande typée permettant à l’application active de revenir
  à son état interne précédent.
- **Home :** retour à l’accueil Mobile sans terminer automatiquement l’application.
- **Recent :** surface affichant les applications/fenêtres mobiles récentes.

## 3. Modèle de plateformes extensible

### 3.1 Ne pas utiliser un enum fermé

Introduire un identifiant validé, par exemple `AppPlatformId`, contenant une chaîne
normalisée :

- minuscules ASCII ;
- segments séparés par `-` si nécessaire ;
- longueur bornée ;
- valeurs initiales connues : `desktop` et `mobile`.

Le catalogue des plateformes connues appartient au système de policy/build, pas à
un `switch` compilé dans chaque application. Ajouter une troisième plateforme
doit principalement nécessiter :

1. l’enregistrement de son identifiant et de ses capacités de shell ;
2. une implémentation de shell ;
3. des points d’entrée d’applications compatibles ;
4. des tests et un profil de build.

Les manifestes existants ne doivent pas devenir invalides simplement parce qu’une
nouvelle plateforme est ajoutée.

### 3.2 Capacités décrites par la plateforme

Une plateforme enregistrée expose un descripteur tel que :

- identifiant ;
- famille de shell ;
- support des fenêtres flottantes ;
- nombre de surfaces principales visibles ;
- support move/resize/minimize/maximize ;
- type de navigation système ;
- présence d’une barre d’application ;
- stratégie de clavier ;
- contraintes de viewport et safe areas.

Le code applicatif interroge ces capacités par contrat lorsqu’il doit adapter un
composant partagé. Il ne doit pas multiplier les tests directs
`if (platform == mobile)` lorsqu’une capacité exprime mieux le besoin.

## 4. Manifeste unique et points d’entrée multiples

### 4.1 Forme proposée

Faire évoluer le schéma de manifeste vers une collection de points d’entrée. Une
entrée peut couvrir une ou plusieurs plateformes :

```json
{
  "id": "org.hackeros.example",
  "kind": "window",
  "platform": {
    "supported": ["desktop", "mobile"],
    "entryPoints": [
      {
        "platforms": ["desktop", "mobile"],
        "assembly": "HackerOs.Apps.Example.dll",
        "type": "HackerOs.Apps.Example.SharedExampleApp"
      }
    ]
  }
}
```

Application utilisant deux composants distincts avec le même `AppId` :

```json
{
  "id": "org.hackeros.example",
  "kind": "window",
  "platform": {
    "supported": ["desktop", "mobile"],
    "entryPoints": [
      {
        "platforms": ["desktop"],
        "assembly": "HackerOs.Apps.Example.dll",
        "type": "HackerOs.Apps.Example.DesktopExampleApp"
      },
      {
        "platforms": ["mobile"],
        "assembly": "HackerOs.Apps.Example.dll",
        "type": "HackerOs.Apps.Example.MobileExampleApp"
      }
    ]
  }
}
```

Une application Desktop seulement déclare uniquement `desktop`. Elle n’apparaît
pas lorsque `mobile` est actif. La règle est symétrique pour une application
Mobile seulement.

### 4.2 Règles de validation

- `supported` contient au moins une plateforme connue par le profil de build.
- Chaque plateforme supportée est couverte par exactement un point d’entrée.
- Un point d’entrée peut couvrir plusieurs plateformes.
- Deux points d’entrée ne peuvent pas couvrir la même plateforme pour le même
  manifeste, afin d’éviter une résolution ambiguë.
- Un point d’entrée ne peut référencer une plateforme absente de `supported`.
- Tous les points d’entrée doivent correspondre au `kind` déclaré et respecter les
  mêmes règles de base/visibilité/trimming.
- L’identité, la version, les permissions, les settings, les associations et les
  données appartiennent au manifeste/AppId commun, pas à chaque point d’entrée.
- Les ressources peuvent être communes ou ciblées par plateforme avec une
  déclaration explicite.
- Les dépendances doivent être résolues pour la plateforme active ; une dépendance
  obligatoire indisponible rend l’application indisponible sur cette plateforme.

### 4.3 Compatibilité et migration

- Introduire une nouvelle version du schéma de manifeste.
- Migrer l’ancien champ `entryPoint` vers une entrée couvrant `desktop` par défaut.
- Fournir une erreur claire si un ancien manifeste est lancé en Mobile sans
  déclaration compatible.
- Mettre à jour le JSON Schema, le contexte `System.Text.Json`, les fixtures, le
  validateur CLI, les templates et les guides SDK.
- Ne pas maintenir durablement deux sources de vérité indépendantes
  (`entryPoint` et `entryPoints`). La compatibilité de lecture peut être
  temporaire, mais la sérialisation canonique utilise le nouveau modèle.

## 5. Résolution du point d’entrée

Ajouter un service indépendant du shell, par exemple
`IAppPlatformEntryPointResolver` :

```text
Manifest + plateforme active
  -> validation de compatibilité
  -> point d’entrée unique
  -> découverte du type
  -> descripteur effectif
  -> lancement
```

Le catalogue conserve une identité d’application unique. Il ne doit pas créer deux
applications cataloguées pour les variantes Desktop et Mobile.

La résolution par plateforme intervient avant :

- l’affichage dans le launcher ;
- la sélection d’un handler de fichier ;
- la résolution d’un intent ;
- le chargement lazy d’une assembly ;
- la création d’un processus et d’une surface ;
- le contrôle singleton.

Conséquences attendues :

- une application incompatible n’apparaît pas dans le launcher actif ;
- elle n’est pas proposée dans Open With ;
- un intent explicite vers celle-ci retourne un résultat typé
  `PlatformUnsupported` ;
- ses services peuvent être traités séparément si leur point d’entrée n’est pas une
  surface UI et si le manifeste les déclare compatibles ;
- le même `AppId` conserve settings, grants, associations et données lors d’un
  changement de plateforme.

## 6. Sélection et persistance du mode OS

### 6.1 Modèle de préférence

Persister une préférence par appareil :

```text
UiPlatformPreference
  SelectionSource: Auto | Explicit
  ExplicitPlatformId: string?     # requis seulement pour Explicit
  LastResolvedPlatformId: string  # diagnostic, pas source d’autorité
  Revision
```

Cette préférence doit être stockée dans un document de settings device-scoped du
shell système, par exemple sous l’AppId protégé `org.hackeros.shell`. Elle ne doit
pas suivre l’utilisateur en roaming, car le choix est propre au device/navigateur.

### 6.2 Détection automatique

Créer `IPlatformEnvironmentProbe` derrière une implémentation Browser. La décision
automatique combine des signaux plutôt que la seule largeur :

- viewport et écran rapportés ;
- pointer coarse/fine ;
- présence de hover ;
- touch points ;
- mode PWA/standalone ;
- user-agent client hints lorsque disponibles, utilisés comme indice et non comme
  autorité unique.

La politique retourne un résultat motivé et journalisable. Exemple :

```text
mobile si environnement tactile/coarse sans hover et largeur logique sous le seuil
desktop autrement
```

Les seuils sont configurables et testés. Un redimensionnement ponctuel d’une
fenêtre Desktop ne doit pas être confondu avec un changement d’appareil.

### 6.3 Changement explicite

Le mode peut être changé dans Settings et, éventuellement, dans une action rapide
du shell. Lors d’un changement explicite :

1. valider que la plateforme cible est installée et activée ;
2. prévenir les applications UI et demander confirmation pour les états sales ;
3. annuler proprement les opérations de surface en cours ;
4. arrêter les instances UI avec une raison typée `PlatformChanged` ;
5. conserver les services compatibles qui ne dépendent pas du shell, ou les
   redémarrer si leur contrat l’exige ;
6. persister la préférence device-scoped de façon atomique ;
7. reconstruire le shell cible ;
8. résoudre à nouveau le launcher, les associations et les points d’entrée ;
9. afficher Home sur Mobile ou le bureau sur Desktop.

Ne pas remplacer à chaud le type de composant d’une instance existante. Une
instance Desktop et une instance Mobile peuvent partager le même `AppId`, mais le
changement de point d’entrée passe par un arrêt/re-lancement contrôlé afin de
respecter le cycle de vie et les ressources.

Fournir une action « Utiliser la détection automatique » qui supprime l’override
explicite et revient au résultat du navigateur.

## 7. Shell Mobile

### 7.1 Fenêtres plein écran

En Mobile :

- une seule surface principale est visible ;
- ses bounds correspondent à la work area entre safe areas, barre système et
  clavier virtuel éventuel ;
- déplacement, redimensionnement, minimisation par chrome, maximisation et
  restauration sont indisponibles ;
- les dialogues sont plein écran ou présentés comme feuilles modales adaptées ;
- les fenêtres secondaires appartenant à l’application sont représentées dans la
  pile de navigation plutôt que comme fenêtres flottantes lorsque possible ;
- l’état processus et lifecycle reste géré par le même noyau.

Le moteur exportable ne doit pas simuler une fenêtre 1280x720 réduite. Le mode de
présentation doit appliquer une vraie politique `SingleFullScreenSurface`.

### 7.2 Barre système inspirée d’Android

Créer `MobileSystemNavigationBar.razor` et son fichier CSS collocaté. La barre est
fixée en bas, respecte `env(safe-area-inset-bottom)` et expose trois boutons avec
labels accessibles :

- **Triangle — Back** ;
- **Cercle — Home** ;
- **Carré — Recent**.

L’inspiration visuelle correspond à la navigation Android classique, sans copier
des assets propriétaires. Utiliser des formes CSS/SVG appartenant au projet,
dimensionnées pour une cible tactile d’au moins 44×44 CSS pixels.

### 7.3 Sémantique Back

Ordre de traitement du triangle Mobile :

1. fermer ou revenir dans le dialogue/modal système supérieur s’il est annulable ;
2. appeler le handler Back de l’application active si elle expose explicitement ce
   support et peut revenir ;
3. revenir à l’entrée précédente de la pile de navigation de l’application ou du
   shell ;
4. si aucun état précédent n’existe, fermer/retirer la surface active selon sa
   politique de fermeture et revenir à Home ;
5. à Home sans historique, ne rien faire et produire un retour accessible discret.

Le Back ne doit pas contourner une confirmation de données non sauvegardées.

### 7.4 Sémantique Home

- Masquer la surface active et afficher l’accueil Mobile.
- Ne pas terminer automatiquement le processus.
- Publier un événement de blur/background dans la session OS.
- Respecter la politique de suspension simulée et les limites de ressources.
- Un nouvel appui sur l’icône de l’application restaure son instance singleton ou
  en crée une selon son manifeste.

### 7.5 Sémantique Recent

- Afficher les instances UI récentes compatibles avec la plateforme active.
- Présenter un aperçu sûr, le titre et l’icône sans exposer de contenu sensible
  lorsqu’une application demande la protection des previews.
- Permettre activation et fermeture avec confirmation éventuelle.
- Ne pas confondre Recent avec la liste de tous les processus/services.
- Exclure les applications devenues incompatibles ou désactivées après une mise à
  jour de policy.

## 8. Contrat Back applicatif

Ajouter un contrat SDK sans dépendance au composant de shell, par exemple :

```csharp
public interface IAppBackHandler
{
    bool CanNavigateBack { get; }
    ValueTask<AppBackResult> NavigateBackAsync(
        AppBackRequest request,
        CancellationToken cancellationToken);
}
```

Le résultat est typé : `Handled`, `NotHandled`, `Blocked`, `ConfirmationRequired`
ou `Faulted`. Les changements de `CanNavigateBack` sont observables afin que le
shell mette à jour ses contrôles sans polling.

Le manifeste déclare explicitement le support Back pour chaque point d’entrée ou
pour l’application partagée. La découverte valide que le type implémente le
contrat annoncé.

### Desktop

- Si l’application déclare Back, afficher un bouton Back dans sa barre de menu ou
  barre applicative Desktop.
- Le bouton est désactivé lorsque `CanNavigateBack` est faux.
- Il n’est pas ajouté au chrome système des applications qui ne le déclarent pas.
- Le raccourci clavier documenté utilise le même contrat.

### Mobile

- Ne pas dupliquer le bouton Back dans la barre applicative.
- Le triangle système appelle le même contrat.
- Une application peut rendre sa propre navigation interne, mais ne doit pas
  masquer ou intercepter silencieusement la barre système.

## 9. Terminal et clavier virtuel Mobile

### 9.1 Principe

Sur Mobile, le Terminal utilise un clavier virtuel HackerOS intégré à sa surface.
Le clavier natif du téléphone ne doit pas s’ouvrir pour la zone terminal.

Créer une abstraction de saisie terminal commune afin que clavier physique,
clavier virtuel et tests produisent les mêmes événements typés vers la session.

### 9.2 Fonctionnalités minimales

Le clavier virtuel doit fournir :

- disposition lettres/chiffres ;
- disposition symboles utile au shell (`/`, `-`, `_`, `.`, `|`, `>`, `<`, `~`,
  `*`, quotes et backslash) ;
- Shift et verrouillage de majuscules ;
- Ctrl, Alt/Meta selon la politique du terminal ;
- Escape, Tab, Enter et Backspace ;
- flèches directionnelles, Home/End et Page Up/Page Down ;
- raccourcis utiles comme Ctrl+C, Ctrl+D, Ctrl+L et Ctrl+R ;
- état visuel des modificateurs ;
- labels accessibles et retour tactile/visuel configurable ;
- répétition bornée pour Backspace et flèches ;
- internationalisation des labels sans changer les caractères du shell.

### 9.3 Intégration navigateur

- La surface terminal ne doit pas dépendre d’un `<input>` texte qui déclenche le
  clavier natif.
- Utiliser `inputmode="none"`, focus programmatique et interop minimale seulement
  si les moteurs mobiles l’exigent ; tester réellement Chrome Android et la
  politique Safari/iOS retenue.
- Prévoir un fallback explicite si un navigateur force tout de même son clavier,
  sans créer deux sources de saisie concurrentes.
- Quand le clavier HackerOS apparaît, recalculer la work area du terminal et son
  nombre de lignes/colonnes sans recouvrir le prompt.
- Les changements d’orientation et safe areas déclenchent un événement de resize
  terminal borné et annulable.
- Le clavier ne doit jamais recevoir ou conserver le contenu sensible hors de la
  session active.

### 9.4 Desktop

Le Terminal Desktop conserve clavier physique et comportement actuel. Un clavier
virtuel facultatif peut être exposé comme outil d’accessibilité, mais il n’est pas
activé automatiquement.

## 10. Cycle de vie et données lors du changement de plateforme

- Le changement de plateforme ne change jamais `AppId`.
- Les grants, settings, données privées et associations restent associés à
  l’application commune.
- Les settings de présentation propres à une plateforme utilisent des clés ou
  documents distincts sous le même AppId.
- La géométrie Desktop n’est pas appliquée en Mobile.
- L’historique Recent Mobile n’est pas interprété comme un placement de fenêtre
  Desktop.
- Les intents et fichiers en attente sont conservés seulement si leur contrat est
  sérialisable et si le point d’entrée cible les accepte.
- Les états volatils de composant ne sont pas transférés implicitement entre deux
  types d’entrée. Une application peut fournir une migration/reconstruction
  explicite via son modèle persistant.
- Si une application active ne supporte pas la plateforme cible, demander une
  confirmation si elle contient un état sale, puis l’arrêter et la retirer des
  surfaces visibles.

## 11. Accessibilité, orientation et responsive design

- Respecter WCAG 2.2 AA, zoom et tailles de texte utilisateur.
- Cibles tactiles d’au moins 44×44 CSS pixels.
- Navigation utilisable avec lecteur d’écran et clavier externe.
- Annoncer les changements Home/Recent/Back et le changement de plateforme.
- Supporter portrait et paysage sans recouvrir contenu, dialogues ou terminal.
- Utiliser les safe-area insets pour appareils avec encoche ou barre système.
- Ne pas supposer une largeur fixe ni identifier Mobile uniquement par user-agent.
- Tester les textes longs, traductions, RTL selon la décision produit, contraste,
  mouvement réduit et grossissement 200/400 %.

## 12. Sécurité et autorité

- Changer le mode de présentation est un setting device-scoped autorisé à
  l’utilisateur du device ; modifier les plateformes installées/activées reste une
  opération Administrateur/System.
- Une application ne peut pas forcer le mode global sans intent/capability dédié et
  confirmation utilisateur.
- L’identifiant de plateforme fourni par le navigateur n’accorde aucune capacité.
- Une variante Mobile n’obtient pas plus de permissions que la variante Desktop du
  même AppId.
- Les previews Recent respectent les contenus sensibles.
- Le clavier terminal ne doit pas injecter de commandes autrement que par le flux
  d’entrée typé de la session active.
- Le routage de point d’entrée valide toujours assembly, type, AppId, kind,
  plateforme, profil de build et compatibilité SDK avant instanciation.

## 13. Tests requis

### Manifestes et catalogue

- point d’entrée unique couvrant Desktop et Mobile ;
- deux points d’entrée distincts sous le même AppId ;
- application Desktop seulement cachée en Mobile et inversement ;
- couverture manquante, doublon ou plateforme inconnue rejetés ;
- troisième plateforme fictive enregistrée sans modification du parseur ;
- résolution d’intents, associations et dépendances selon la plateforme ;
- compatibilité/migration des manifestes historiques.

### Mode OS

- premier démarrage Auto Desktop et Auto Mobile ;
- choix explicite persisté par device ;
- retour à Auto ;
- changement de plateforme avec applications propres ou sales ;
- services compatibles et incompatibles ;
- reconstruction après reload ;
- absence de fuite d’état entre deux devices/utilisateurs.

### Shell Mobile

- surface toujours plein écran en portrait/paysage ;
- aucun drag/resize/chrome Desktop actif ;
- Back : dialogue, app, historique, fermeture et Home ;
- Home conserve le processus selon sa policy ;
- Recent active et ferme les bonnes instances ;
- safe areas, lecteur d’écran, clavier externe et focus ;
- disparition immédiate des applications incompatibles/désactivées.

### Terminal Mobile

- aucun clavier natif à l’ouverture et au focus ;
- saisie lettres, symboles et modificateurs ;
- Ctrl+C, Ctrl+D, Tab, Escape, flèches et répétition ;
- redimensionnement du viewport lorsque le clavier apparaît ;
- orientation, reconnexion, annulation et fermeture ;
- commandes ordinaires et applications terminal full-screen ;
- absence de double événement entre clavier physique et virtuel.

### Navigateur/PWA

- Chrome Android réel ou émulation documentée ;
- stratégie et preuves Safari/iOS selon la matrice supportée ;
- installation PWA, reload hors ligne et update ;
- persistance du mode par device ;
- changement Desktop/Mobile sans mélange d’assets ou de points d’entrée ;
- console et réseau sans erreur.

## 14. Plan d’implémentation

- [x] `MOB-001` Créer `AppPlatformId` et le registre extensible de plateformes.
  **Phase 0, 2026-08-18** — `Shared/HackerOs.App.Abstractions/AppPlatformId.cs`
  (type validé + `WellKnownAppPlatforms.Desktop`/`.Mobile`). Pas encore de
  descripteurs de capacités (`MOB-002`, toujours ouvert).
- [x] `MOB-002` Définir les descripteurs de capacités de plateforme et les contrats
  du shell. **Phase 1, 2026-08-18** —
  `Shared/HackerOs.App.Abstractions/AppPlatformCapabilities.cs`
  (`AppPlatformCapabilities`, `IAppPlatformCapabilityRegistry`/
  `AppPlatformCapabilityRegistry`, descripteurs `WellKnownAppPlatformCapabilities.Desktop`/
  `.Mobile`). Pas encore consommé par une UI de shell (ça reste Phase 2, `MOB-009`/`010`).
- [x] `MOB-003` Faire évoluer le manifeste vers plusieurs points d’entrée couvrant
  une ou plusieurs plateformes. **Phase 1, 2026-08-18** —
  `Shared/HackerOs.App.Abstractions/AppManifestPlatform.cs`
  (`AppManifestPlatform`, `AppPlatformEntryPointManifest`,
  `AppManifestPlatformSupport.Resolve` normalizer). `AppManifest.EntryPoint` est
  devenu optionnel ; `AppManifest.Platform` est le nouveau champ, mutuellement
  exclusif avec `EntryPoint` (validé par `AppManifestValidator`). Un manifeste
  historique n’utilisant que `entryPoint` est traité comme couvrant `desktop`
  seul, sans qu’aucun des 40+ `app.manifest.json` existants n’ait eu besoin
  d’être modifié — voir §16.5.
- [x] `MOB-004` Mettre à jour JSON Schema, sérialisation source-generated,
  validation, fixtures, CLI et templates. **Phase 1, 2026-08-18** — schéma :
  `Shared/HackerOs.App.Abstractions/Schema/manifest.schema.v1.json` (`platform`
  optionnel + règle `oneOf` entryPoint/platform) ; sérialisation :
  `AppManifestJsonSerializerContext` + tri canonique dans
  `AppManifestJsonSerializer.CreateCanonicalManifest` ; validation : voir
  `MOB-003` ; fixtures : `window-multi-platform.valid.json` et
  `invalid-platform-ambiguous.json` sous `Schema/Fixtures/` ; CLI :
  `Tools/HackerOs.Tools.ManifestValidator` fonctionne sans modification (le
  générateur source inclut `AppManifestPlatform` automatiquement, type atteint
  depuis `AppManifest`), vérifié en validant un manifeste `platform` réel.
  Pas de projet de template `dotnet new` trouvé dans le dépôt pour ce lot —
  aucun à mettre à jour.
- [x] `MOB-005` Implémenter `IAppPlatformEntryPointResolver` et l’intégrer à la
  découverte, au catalogue, aux intents et aux associations. **Phase 1,
  2026-08-18 — tranche découverte seule** :
  `Platform/HackerOs.Platform.Core/Discovery/AppPlatformEntryPointResolver.cs`
  + `AppEntryPointDiscovery.Discover` prend désormais un `activePlatform`
  optionnel (défaut `desktop`, comportement inchangé pour tout appelant
  existant). Un manifeste ne supportant pas la plateforme demandée est exclu du
  résultat sans erreur, par §5. L’intégration aux intents/associations
  (`FileAssociationResolver`, `AppIntentDispatcher`) est volontairement
  **reportée à la Phase 2** (`MOB-008`) : filtrer ces surfaces sur la
  préférence de plateforme *actuelle* de l’utilisateur avant que le shell sache
  reconstruire proprement (arrêt des instances sales, etc.) masquerait toutes
  les apps existantes dès qu’un utilisateur bascule le toggle Mobile de la
  Phase 0, sans qu’aucun shell Mobile n’existe encore pour les reprendre — voir
  §16.5.
- [x] `MOB-006` Ajouter la préférence `Auto | Explicit` device-scoped et sa
  migration. **Phase 0, 2026-08-18 — tranche persistance seule** :
  `Platform/HackerOs.Platform.Core/Shell/UiPlatformPreferenceSettingsDocuments.cs`
  + `UiPlatformPreferenceService.cs` (scope `OsAdmin`/`SyncEligible:false` en
  attendant un `InstallationId` durable pour un vrai scope `AppDevice`, voir
  §16.3), exposée dans le panneau horloge (§16.4). La séquence contrôlée en 9
  étapes de §6.3 (avertir les apps sales, arrêter les instances UI avec la
  raison `PlatformChanged`, reconstruire le shell...) reste à faire — ce lot ne
  fait volontairement rien de plus que persister le choix et notifier un futur
  abonné, sans jamais changer le rendu de `DesktopShell`.
- [x] `MOB-007` Implémenter `IPlatformEnvironmentProbe` Browser avec raisons de
  décision testables. **Phase 0, 2026-08-18** —
  `Platform/HackerOs.Platform.Core/Shell/IPlatformEnvironmentProbe.cs`
  (`PlatformEnvironmentPolicy.Decide`, pur et testable sans navigateur) +
  `Platform/HackerOs.Platform.Blazor/Shell/BrowserPlatformEnvironmentProbe.cs`
  (interop JS minimal, `wwwroot/platformEnvironmentProbe.js`).
- [x] `MOB-008` Implémenter le changement contrôlé de shell et la raison d’arrêt
  `PlatformChanged`. **Phase 2b, 2026-08-18 — tranche pragmatique, pas les 9
  étapes littérales de §6.3** :
  `Platform/HackerOs.Platform.Blazor/Shell/PlatformShellSwitchCoordinator.cs`
  confirme chaque fenêtre ouverte via `WindowCloseGuardRegistry`, arrête son
  instance avec `ProcessExitReason.PlatformChanged` (nouveau, dans
  `Shared/HackerOs.Simulation.Abstractions`) puis ferme sa fenêtre, avant de
  persister via `UiPlatformPreferenceService`. `ClockPanel.razor` route
  désormais Auto/Desktop/Mobile par ce coordinateur plutôt que d’appeler
  `UiPlatformPreferenceService` directement, pour confirmer *avant* de
  persister (ordre exact de §6.3). `App.razor` s’abonne à
  `UiPlatformPreferenceService.Changed` et rend `MobileShell` ou `DesktopShell`
  selon `Current.ActivePlatform` — le changement de shell est donc réellement
  en direct, sans reload, prouvé par un test E2E navigateur réel
  (`PlatformShellSwitchTests.cs`). Détail des simplifications assumées en §16.7.
- [x] `MOB-009` Créer le shell Mobile à surface unique plein écran. **Phase 2a,
  2026-08-18** — moteur : `Platform/HackerOs.Windowing.Core/SingleSurfacePresentationPolicy.cs`
  (logique pure : sélectionne la fenêtre principale, minimise les autres,
  réutilise Maximize pour remplir la zone de travail) +
  `WindowConstraints.IsMovable` (nouveau, appliqué par `WindowRuntime.Move`,
  défense en profondeur). Blazor : `Platform/HackerOs.Windowing.Blazor/SingleSurfaceArea.razor`
  (réutilise `WindowHost` avec son nouveau paramètre `ShowChrome=false`) +
  `Platform/HackerOs.Platform.Blazor/Shell/MobileShell.razor` (compose la
  surface unique et la barre système). En corrigeant un bug latent découvert
  pendant le spike de faisabilité (`WindowChrome` ignorait déjà `IsResizable`
  pour les dialogues de fichiers non redimensionnables), voir §16.6.
- [x] `MOB-010` Créer `MobileSystemNavigationBar.razor/.css` avec Back/Home/Recent.
  **Phase 2a, 2026-08-18** — nouveau package `Platform/HackerOs.MobileShell.Blazor/`
  (frère de `HackerOs.Taskbar.Blazor`, comme prévu en §16.2), contrat
  `IMobileNavigationCommands`. `Back`/`Recent` sont des no-op documentés tant
  que `MOB-011`/`MOB-012` n’existent pas ; `Home` masque la surface active via
  Minimize sans terminer le processus (§7.4), implémenté dans
  `MobileNavigationCommandsAdapter`.
- [x] `MOB-011` Implémenter la pile de navigation et la surface Recent.
  **2026-08-23** — voir §16.10 pour le détail fichier par fichier.
- [x] `MOB-012` Ajouter `IAppBackHandler`, ses événements et la validation de
  manifeste. **Phase 2c, 2026-08-19** — voir §16.9 pour le détail fichier par
  fichier (`AppBackContracts.cs`, `AppManifest.SupportsBack`,
  `AppBackHandlerRegistry`). `CanNavigateBackChanged` n'est pas encore câblé au
  chrome (voir l'écart documenté en fin de §16.9).
- [x] `MOB-013` Ajouter le bouton Back Desktop uniquement aux applications qui le
  déclarent. **Phase 2c, 2026-08-19** — voir §16.9 (`WindowChrome`, Desktop
  uniquement ; Mobile ne le duplique pas par construction, `ShowChrome=false`).
- [x] `MOB-014` Ajouter les variantes Desktop/Mobile de référence sous un AppId
  commun et une application à point d’entrée partagé. **Phase 1, 2026-08-18 —
  tranche partielle : un seul point d’entrée partagé** (le premier exemple de
  §4.1, pas encore les deux variantes distinctes du second exemple) —
  `Apps/Samples/HackerOs.Samples.PlatformApp/` (`PlatformDemoWindow.razor` +
  `app.manifest.json` déclarant `platform.entryPoints` pour `desktop`+`mobile`
  via le même type) et `Tests/HackerOs.Samples.PlatformApp.Tests/` (validation,
  résolution par plateforme, et cohérence du fichier JSON archivé — tout passe
  par le vrai `AppManifestValidator`/`AppCatalog`/`AppEntryPointDiscovery`).
  Comme les samples existants, ce projet n’est pas câblé dans le catalogue de
  build de `HackerOs.Ecosystem` — c’est un projet de démonstration/test isolé,
  pas une app réellement lancable dans le shell.
- [x] `MOB-015` Implémenter le clavier virtuel HackerOS du Terminal Mobile.
  **2026-08-23** — généralisé en un contrôle réutilisable indépendant du
  Terminal, voir §16.11.
- [ ] `MOB-016` Adapter le viewport terminal, les safe areas et l’orientation.
- [ ] `MOB-017` Ajouter tests unitaires, composants, Playwright, accessibilité et
  PWA pour la matrice complète.
- [ ] `MOB-018` Mettre à jour SDK, guide développeur, manifests existants, ADRs,
  liste d’intégration et documentation utilisateur.

## 15. Définition de complétion

Le support Mobile est terminé lorsque :

- une application commune fonctionne avec un seul point d’entrée sur Desktop et
  Mobile ;
- une seconde application utilise deux points d’entrée sous un même AppId ;
- les applications incompatibles sont absentes de toutes les surfaces de
  découverte et retournent une erreur typée en lancement explicite ;
- une troisième plateforme factice peut être enregistrée et résolue sans modifier
  le format de manifeste ;
- le choix explicite est persisté par device et Auto suit la détection navigateur ;
- le passage Desktop/Mobile respecte cycle de vie, états sales et données ;
- les applications Mobile sont toujours plein écran ;
- Triangle, Cercle et Carré implémentent exactement Back, Home et Recent ;
- le Back applicatif est explicite, testable et affiché dans la barre d’application
  Desktop seulement ;
- le Terminal Mobile fonctionne avec le clavier HackerOS sans clavier natif ;
- la matrice unit/component/browser/PWA/accessibilité passe en Release ;
- tous les manifestes, guides SDK et documents d’architecture sont synchronisés.

## 16. Feuille de route par phases (ajouté 2026-08-18)

### 16.1 Ce que « Phase 0 » a livré

Voir `MOB-001`/`006`/`007` ci-dessus pour le détail fichier par fichier. En plus
de ces trois tâches, Phase 0 a ajouté une brique qui n’était pas nommée dans ce
document à l’origine : un point d’entrée UI réel pour le choix de plateforme.
`HackerOs.Taskbar.Blazor.Taskbar` expose désormais `ITaskbarClockPanelSource` +
un paramètre `RenderFragment? ClockPanelContent` — le taskbar possède
uniquement le déclencheur (bouton horloge) et l’ancrage du panneau, jamais son
contenu. `Platform/HackerOs.Platform.Blazor/Shell/ClockPanel.razor` (fourni par
l’application hôte, hors du package taskbar) combine notifications, un
calendrier minimal, et le sélecteur Auto/Desktop/Mobile branché sur
`UiPlatformPreferenceService`. Ce même changement a fusionné l’ancienne cloche
de notifications (`ITaskbarNotificationSource`, dont le panneau ne s’affichait
jamais — bug préexistant) dans ce nouveau point d’entrée unique.

Bascule le toggle ne change **pas** encore le rendu du shell : c’est une
persistance de préférence uniquement, avec un événement `Changed` prêt à être
consommé par une phase future (`MOB-008`/`009`).

### 16.2 Rattachement aux packages réels

Ce document, écrit avant l’extraction fenêtres/taskbar, ne nomme jamais les
packages qui portent désormais les concepts qu’il décrit :

- **§3 (descripteurs de capacités, `MOB-002`)** : contrat à définir aux côtés de
  `HackerOs.Windowing.Abstractions` (mêmes conventions que `WindowId`/
  `WindowBounds`/`WindowVisualState`).
- **§7.1 (`SingleFullScreenSurface`, `MOB-009`)** : politique de présentation à
  ajouter à `HackerOs.Windowing.Core`/`WindowRuntime` — **non vérifié** que le
  moteur actuel (conçu pour des fenêtres flottantes) supporte une surface
  unique non déplaçable/non redimensionnable ; un spike est nécessaire avant de
  chiffrer `MOB-009` en détail.
- **§7.2 (`MobileSystemNavigationBar.razor`, `MOB-010`)** : nouveau package
  frère de `HackerOs.Taskbar.Blazor` (Mobile n’a pas de taskbar), pas une
  modification de celui-ci.
- **§6 (préférence et détection, `MOB-006`/`007`)** : fait en Phase 0, voir
  §16.1.

### 16.3 Risque ouvert : `InstallationId` non durable

`UiPlatformPreference` (§6.1) doit rester local à l’appareil et ne jamais se
synchroniser entre les appareils d’un même utilisateur. Le scope
`SettingsScope.AppDevice` existe déjà pour ça, mais
`EcosystemServiceCollectionExtensions.cs` régénère `InstallationId` à chaque
démarrage (`Guid.NewGuid()`, jamais persisté) — l’utiliser casserait la
persistance à chaque rechargement de page. Phase 0 utilise donc le scope
`OsAdmin` + `SyncEligible:false` (même résultat pratique : pas de
synchronisation), en notant la migration vers un vrai scope `AppDevice` comme
suite mécanique une fois `InstallationId` rendu durable — pas un blocage pour
la suite de ce plan.

### 16.4 Séquencement proposé pour `MOB-002` à `MOB-018`

```text
Phase 1 — Manifeste et résolution de point d’entrée [FAIT le 2026-08-18]
  MOB-002, MOB-003, MOB-004, MOB-005, MOB-014 (partiel : une app de référence)
  Dépend de : AppPlatformId (Phase 0, fait)

Phase 2 — Shell Mobile et changement contrôlé [FAITE le 2026-08-18]
  Phase 2a (sous-tranche) : MOB-009, MOB-010 — voir §16.6. Spike de faisabilité
  (§16.2) confirmé additif, pas de restructuration du moteur.
  Phase 2b (sous-tranche) : MOB-008, en version pragmatique — voir §16.7.
  Phase 2c (sous-tranche) : MOB-012, MOB-013 (contrat IAppBackHandler, bouton
  Back Desktop, triangle Mobile appelle l'étape 2 de §7.3) — voir §16.9.
  Reste hors Phase 2 : MOB-011 (pile de navigation, Recent — Recent du
  MOB-010 et les étapes 1/3/4 de la séquence Back de §7.3 restent des no-op
  en attendant)
  Dépend de : Phase 1 (les apps doivent déclarer un point d’entrée Mobile)

Phase 3 — Clavier virtuel Terminal Mobile
  MOB-015, MOB-016
  Dépend de : Phase 2 (a besoin d’une vraie surface Mobile à laquelle s’attacher)

Phase 4 — Durcissement
  MOB-017 (matrice de tests complète), MOB-018 (SDK/docs/ADR/manifestes)
  Dépend de : Phases 1-3
```

Ce séquencement est une proposition de dépendances, pas un engagement daté —
à réviser/prioriser au démarrage de chaque phase selon la bande passante
disponible, comme le fait déjà
[`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md) §8 pour
les autres chantiers.

### 16.5 Ce que « Phase 1 » a livré

Voir `MOB-002`/`003`/`004`/`005`/`014` ci-dessus pour le détail fichier par
fichier. Résumé des décisions de conception :

- **Rétrocompatibilité sans migration de fichiers** : plutôt que de migrer les
  40+ `app.manifest.json` existants vers le nouveau format (§4.3 l'envisage
  comme une migration physique), `AppManifest.EntryPoint` est resté un champ
  optionnel à part entière et `AppManifestPlatformSupport.Resolve` normalise
  virtuellement tout manifeste `entryPoint`-only comme couvrant `desktop`
  seul. Un seul point d'entrée normalisé (`AppManifestPlatformResolution`) sert
  ensuite à la fois la validation et la résolution — pas de double source de
  vérité au sens de §4.3, juste deux syntaxes JSON d'entrée pour un seul modèle
  interne.
- **`platform` et `entryPoint` sont mutuellement exclusifs**, imposé à la fois
  par le JSON Schema (règle `oneOf`) et par `AppManifestValidator`
  (`manifest.platform.required`/`manifest.platform.ambiguous`).
- **La découverte reste stable par défaut** : `AppEntryPointDiscovery.Discover`
  résout maintenant chaque manifeste via `IAppPlatformEntryPointResolver` pour
  une plateforme active (nouveau paramètre optionnel, défaut `desktop`), donc
  aucun appelant existant (chargement paresseux WebAssembly compris, voir
  `BuildKnownLazyAppDescriptorRegistry`/`WebAssemblyLazyAssemblyTransport`) n'a
  changé de comportement observable.
- **Périmètre volontairement non couvert par cette phase** : filtrer
  `FileAssociationResolver`/`AppIntentDispatcher`/le launcher sur la
  préférence de plateforme *actuelle* (§5 : « une application incompatible
  n'apparaît pas dans le launcher actif »). Le faire maintenant masquerait
  toutes les apps existantes (aucune ne déclare encore `mobile`) dès qu'un
  utilisateur bascule le toggle Mobile posé en Phase 0, alors qu'aucun shell
  Mobile n'existe pour les reprendre. Cette intégration revient avec le
  changement de shell contrôlé de `MOB-008` (Phase 2), qui sait suspendre/
  arrêter proprement les instances incompatibles avant de les retirer des
  surfaces visibles.
- **`AppPlatformCapabilities` (`MOB-002`) n'est pas encore consommé** par du
  code de shell — Phase 1 ne fait qu'enregistrer les descripteurs Desktop et
  Mobile ; leur premier vrai consommateur sera le shell Mobile de `MOB-009`/
  `MOB-010` (Phase 2).

### 16.6 Ce que « Phase 2a » a livré

Sous-tranche de Phase 2 couvrant `MOB-009` et `MOB-010`, livrée en isolation
avant `MOB-008` (§16.7) — au moment de cette tranche, `MobileShell` n'était
volontairement atteignable depuis aucune route réelle (voir dernier point
ci-dessous, devenu obsolète depuis Phase 2b). `MOB-011` (pile de
navigation/Recent) et `MOB-012` (`IAppBackHandler`) restent non faits. Voir
`MOB-009`/`010` ci-dessus pour le détail fichier par fichier.

- **Spike de faisabilité (§16.2) fait avant tout code** : le moteur fenêtres
  supporte déjà, sans restructuration, les deux mécanismes dont
  `SingleFullScreenSurface` a besoin — `Maximized` remplit exactement la zone
  de travail suivie par `WindowRuntime`, et `Minimized` cache déjà une fenêtre
  de bout en bout (moteur + rendu). `MOB-009` réutilise les deux plutôt que de
  recalculer une géométrie ou un mécanisme de visibilité séparés.
- **Bug latent corrigé en amont** : le spike a découvert que `WindowChrome`
  affichait ses poignées de redimensionnement sans vérifier
  `Constraints.IsResizable`, et que `DesktopShell.HandleGesture` n'avait aucun
  `try/catch` autour de `WindowRuntime.Apply` — un vrai bug (pas seulement
  latent : les dialogues de fichiers créent déjà des fenêtres
  `isResizable:false` via `FileDialogWindowAdapter.cs`) qui aurait levé une
  `InvalidOperationException` non gérée au premier redimensionnement tenté
  d'un dialogue de fichier. Corrigé avant le reste de `MOB-009` pour ne pas
  bâtir sur un mécanisme de contraintes déjà cassé.
- **`WindowConstraints.IsMovable`** (nouveau, défaut `true`) ajouté en défense
  en profondeur aux côtés de `IsResizable` — le moteur n'avait aucun concept
  d'« impossible à déplacer » avant Phase 2a ; `WindowRuntime.Move` lève
  maintenant la même exception que `Resize` pour une fenêtre épinglée.
- **`SingleSurfaceArea` (Blazor) reste dans `HackerOs.Windowing.Core`/`.Blazor`**,
  pas dans un package Mobile-only, parce que le comportement « une seule
  surface visible à la fois » est un mode de présentation générique du moteur
  de fenêtrage (au même titre que le mode flottant), même si Mobile en est le
  premier consommateur — cohérent avec la note §16.2 qui plaçait déjà
  `SingleFullScreenSurface` dans `HackerOs.Windowing.Core`/`WindowRuntime`.
  `WindowHost` gagne un paramètre `ShowChrome` (`true` par défaut, donc aucun
  changement de comportement pour `DesktopArea`) plutôt qu'un nouveau
  composant dupliqué.
- **`HackerOs.MobileShell.Blazor` est un nouveau package frère de
  `HackerOs.Taskbar.Blazor`**, pas une modification de celui-ci, exactement
  comme anticipé en §16.2 — Mobile n'a pas de taskbar. Contrairement aux
  contrats du taskbar (tous optionnels), `MobileSystemNavigationBar.Commands`
  est requis : la barre système n'a pas d'équivalent Desktop où elle
  disparaîtrait proprement quand absente.
- **`MobileShell.razor` (`Platform/HackerOs.Platform.Blazor/Shell/`) compose
  `SingleSurfaceArea` + `MobileSystemNavigationBar`** exactement comme
  `DesktopShell.razor` compose `DesktopArea` + `Taskbar`. Au moment de la
  livraison de Phase 2a, il n'était atteignable depuis aucune route ni service
  de démarrage réel et n'était testé qu'unitairement (build + tests directs
  sur `WindowRuntime`) — Phase 2b (§16.7) l'a rendu réellement atteignable et
  ajoute la preuve E2E navigateur. `MobileNavigationCommandsAdapter`
  n'implémente réellement que `RequestHome` (masque la surface active via
  Minimize, §7.4) ; `RequestBack`/`RequestRecent` restent des no-op documentés —
  §7.3 accepte explicitement « ne rien faire » comme issue terminale valide en
  l'absence de pile de navigation/gestionnaire Back applicatif (`MOB-011`/`012`).
- **`MOB-008` a suivi immédiatement dans la même session, en Phase 2b** — voir
  §16.7 pour le détail : `MobileShell` avait besoin d'exister avant que le
  changement de shell contrôlé ait un sens, donc les deux tranches
  s'enchaînaient naturellement plutôt que d'être séparées par un délai.

### 16.7 Ce que « Phase 2b » (`MOB-008`) a livré

Rend `MobileShell` réellement atteignable : sélectionner Mobile dans le
panneau horloge bascule maintenant le shell rendu **en direct, sans reload**,
et un volet de notifications accessible par glissement depuis le haut de
l'écran Mobile (§16.8) réutilise ce même panneau pour revenir à Desktop.
Prouvé par des tests navigateur réels de bout en bout
(`Tests/HackerOs.UI.E2E.Tests/PlatformShellSwitchTests.cs`,
`ClockPanelMobileToggleTests.cs`, `MobileNotificationShadeSwipeTests.cs`), en
plus des tests unitaires du coordinateur
(`Tests/HackerOs.Platform.Blazor.Tests/Shell/PlatformShellSwitchCoordinatorTests.cs`,
construits contre un vrai `AppLifecycleOrchestrator`/`WindowRuntime`, pas des
doublures).

- **`PlatformShellSwitchCoordinator`** (`Platform/HackerOs.Platform.Blazor/Shell/`)
  implémente une version pragmatique de la séquence en 9 étapes de §6.3, pas
  littérale point par point : pour chaque fenêtre qui a effectivement besoin
  de redémarrer (voir point suivant), confirme via
  `WindowCloseGuardRegistry.ConfirmCloseAsync` (étape 2) ; si tout accepte,
  arrête l'instance propriétaire avec `ProcessExitReason.PlatformChanged`
  (nouveau, étape 4) puis force la fermeture de la fenêtre (couvre l'étape 3 —
  aucune opération de surface ne survit à l'arrêt de son instance) ; enfin
  persiste le nouveau choix via `UiPlatformPreferenceService` (étape 6). Le
  changement effectif de rendu (étape 7) et l'affichage résultant (étape 9)
  sont la responsabilité d'`App.razor`, pas du coordinateur — voir plus bas.
- **Seules les fenêtres qui en ont réellement besoin sont redémarrées** —
  raffinement apporté après coup (le premier jet arrêtait systématiquement
  toutes les fenêtres ouvertes, plus prudent que nécessaire). §6.3 le dit
  explicitement : « ne pas remplacer à chaud le type de composant d'une
  instance existante... le changement de point d'entrée passe par un
  arrêt/re-lancement contrôlé » — c'est le *changement de type* qui exige
  l'arrêt, pas le changement de plateforme en soi. `WindowRuntime` est un
  singleton DI partagé entre `DesktopShell` et `MobileShell` ; une fenêtre
  dont l'app déclare un point d'entrée partagé pour les deux plateformes
  (comme `HackerOs.Samples.PlatformApp`) survit donc telle quelle au
  changement de shell — le nouveau shell la re-présente simplement (avec ou
  sans chrome) sans toucher à l'instance en cours. `SelectWindowsNeedingRestart`
  compare, pour chaque fenêtre, le point d'entrée résolu
  (`AppManifestPlatformSupport.Resolve`) sur la plateforme active contre celui
  de la plateforme cible ; seule une différence (ou une app non supportée sur
  la cible) déclenche confirmation + arrêt. `RequestAutoAsync` reste
  conservateur (arrête tout) puisque la plateforme résultante d'Auto n'est
  pas connue à l'avance sans relancer la détection.
- **Ordre confirmation-avant-persistance respecté** : Phase 0 avait câblé
  `ClockPanel.razor` pour appeler `UiPlatformPreferenceService.SetExplicitAsync`/
  `ClearToAutoAsync` directement, ce qui aurait persisté le choix avant toute
  confirmation. `ClockPanel.razor` appelle maintenant
  `PlatformShellSwitchCoordinator.RequestExplicitAsync`/`RequestAutoAsync` à la
  place ; si une fenêtre refuse la fermeture, rien n'est persisté et rien ne
  change à l'écran (pas de message d'erreur ajouté — le guard de la fenêtre a
  déjà dû afficher sa propre confirmation, même motif que
  `WindowCloseCoordinator.CloseAsync`).
- **`App.razor` devient le point de bascule réel** : il s'abonne désormais à
  `UiPlatformPreferenceService.Changed` (déplacé depuis l'initialisation
  paresseuse de `DesktopShell.OnInitialized`, maintenant appelée au boot de
  l'application) et rend `<MobileShell />` ou `<DesktopShell />` dans le cas
  `EcosystemHostView.Desktop` selon `Current.ActivePlatform`.
- **Étape 5 (services compatibles) volontairement simplifiée** : aucun service
  (`AppKind.Service`) n'est arrêté ni redémarré par ce coordinateur — tous les
  services en cours restent actifs tels quels. Aucun service du dépôt ne
  déclare aujourd'hui d'exigence de redémarrage liée à la plateforme ; ajouter
  cette distinction reste un exercice mécanique pour une session future si un
  tel service apparaît.
- **Étape 8 (re-résolution launcher/associations/intents) toujours différée** —
  la raison documentée en §16.5 reste valide : aucun manifeste embarqué ne
  déclare de point d'entrée Mobile-only (seul l'exemple `HackerOs.Samples.PlatformApp`
  déclare `platform.entryPoints`, partagé desktop+mobile), donc toute app qui
  se lance aujourd'hui se lance identiquement quelle que soit la plateforme
  active — lancer une app Desktop sur Mobile affiche simplement son UI Desktop
  plein écran sans chrome, ce qui est un état honnête et attendu tant
  qu'aucune app ne déclare de variante Mobile réelle.

### 16.8 Volet de notifications Mobile par glissement (post-`MOB-008`)

Ajouté après coup pour remplacer un premier jet de `MOB-008` (un bouton
placeholder « Switch to Desktop » posé dans un coin de `MobileShell`) par
quelque chose de plus proche d'un vrai shell Mobile : un volet accessible en
glissant depuis le haut de l'écran, à l'image d'un panneau de notifications
Android, qui réutilise **le même composant `ClockPanel`** que le panneau
horloge Desktop (notifications, calendrier, et le sélecteur Auto/Desktop/Mobile
— donc le retour vers Desktop se fait depuis ce même volet, sans contrôle
séparé).

- **`MobileShell.razor.js`** (nouveau, colocalisé) expose
  `attachSwipeDownGesture`/`detachSwipeDownGesture`, sur le même modèle pointer
  capture que `WindowChrome.razor.js` — un bouton poignée en haut de l'écran
  (`.mobile-shade-handle`, aussi cliquable/focusable pour l'accessibilité, pas
  seulement glissable) déclenche `[JSInvokable] OnSwipeDownDetected` après un
  glissement vers le bas d'au moins 32px.
- **Bug réel trouvé en testant, pas seulement latent** : sans
  `event.preventDefault()` dans le gestionnaire `pointerdown`, un glissement
  souris réellement fiable (piloté par le système, pas un événement
  synthétique) peut être détourné vers la sélection de texte/le glisser-déposer
  natif du navigateur avant de délivrer la séquence `pointermove` attendue —
  corrigé pour correspondre exactement au motif déjà utilisé et fonctionnel de
  `WindowChrome.razor.js`.
- **Limite de test découverte en pratique** : ni `page.Mouse` (simulation
  d'entrée au niveau système) ni `Locator.DispatchEventAsync` de Playwright ne
  délivrent de façon fiable un vrai `PointerEvent` (avec `pointerId`/`clientY`/
  `button` correctement renseignés) à un gestionnaire personnalisé basé sur
  `setPointerCapture`, dans Chromium headless — confirmé par expérimentation
  directe. `MobileNotificationShadeSwipeTests.cs` construit et distribue de
  vrais `new PointerEvent(...)` via `page.EvaluateAsync` plutôt que de
  s'appuyer sur l'un ou l'autre.
- **`MobileShell` n'injecte plus `PlatformShellSwitchCoordinator` directement**
  — le volet réutilisant `ClockPanel` tel quel, c'est ce composant qui gère
  déjà tout le flux de changement de plateforme (Phase 2b ci-dessus).

### 16.9 Contrat Back applicatif (`MOB-012`/`MOB-013`, post-§16.8)

Livre le contrat SDK et l'étape 2 de la séquence Back ordonnée de §7.3 —
« appeler le handler Back de l'application active si elle expose explicitement
ce support ». `MOB-011` (pile de navigation, surface Recent) reste
délibérément hors périmètre : c'est un sous-système plus large qui mérite sa
propre tranche dédiée, pas un ajout incrémental à celle-ci.

- **`Shared/HackerOs.AppSdk.Blazor/AppBackContracts.cs`** (nouveau) — le
  contrat exact de §8 : `AppBackSource` (`SystemNavigationBar` /
  `DesktopChromeButton` / `KeyboardShortcut`), `AppBackRequest`,
  `AppBackResultStatus` (`Handled`/`NotHandled`/`Blocked`/
  `ConfirmationRequired`/`Faulted`), `AppBackResult`, et `IAppBackHandler`
  (`CanNavigateBack`, `CanNavigateBackChanged`, `NavigateBackAsync`).
- **`AppManifest.SupportsBack`** (nouveau bool, `Window` uniquement — même
  garde-fou que `FileHandlers`) déclare le support Back pour un point d'entrée
  ou l'application partagée, comme demandé par §8. Le raccourci clavier
  documenté dans §8 (« Le raccourci clavier documenté utilise le même
  contrat ») n'est pas câblé dans cette tranche.
- **`AppEntryPointDiscovery` valide la conformité par réflexion**, sans
  référence de projet vers `HackerOs.AppSdk.Blazor` — même motif exact que
  `DerivesFrom` pour les types de base : un nouveau helper `Implements`
  compare `Type.GetInterfaces()` par nom complet. `manifest.SupportsBack ==
  true` sans un type qui implémente `IAppBackHandler` échoue avec
  `discovery.backHandler.missing`.
- **`WindowRuntimeState.SupportsBack`** — champ déclaratif posé une seule fois
  à la création de la fenêtre depuis `AppManifest.SupportsBack`
  (`WindowLaunchCoordinator`), pas un miroir en direct de
  `IAppBackHandler.CanNavigateBack`.
- **Bug réel trouvé par le test, pas seulement latent** :
  `WindowRuntime.Copy()` (le helper privé que `Create()` utilise pour poser
  `zOrder`/`isFocused` sur l'état initial) oubliait de reporter
  `SupportsBack` — toute fenêtre créée avec `supportsBack: true` perdait
  silencieusement ce fait dès sa création. Détecté par
  `WindowLaunchCoordinatorTests.Present_projects_the_manifests_supportsBack_declaration_onto_the_window`,
  qui teste au niveau `WindowRuntime` réel (pas un double), corrigé en une
  ligne.
- **`AppBackHandlerRegistry` + capture dans `WindowAppRenderer`** — même motif
  exact que `WindowCloseGuardRegistry`/`IWindowCloseGuard` (déjà en place pour
  la fermeture) : `AddComponentReferenceCapture` récupère le composant rendu,
  et s'il implémente `IAppBackHandler`, l'enregistre par `WindowId` dans un
  registre DI singleton. Enregistrements sûrs par référence — la disposition
  d'un ancien renderer ne peut pas retirer le handler d'un composant plus
  récent pour la même fenêtre.
- **Bouton Back dans `WindowChrome`, Desktop uniquement** — rendu
  conditionnellement sur `Window.SupportsBack`, dans `.window-identity` avant
  l'icône. Propagé `WindowChrome` → `WindowHost` → `DesktopArea` →
  `DesktopShell.HandleBack`, qui appelle
  `BackHandlers.NavigateBackAsync(id, new AppBackRequest(DesktopChromeButton))`.
  Mobile ne duplique pas ce bouton par construction, pas par condition
  explicite : `SingleSurfaceArea` rend toujours `WindowHost` avec
  `ShowChrome="false"`, donc `WindowChrome` (et son bouton Back) n'y est
  jamais instancié — conforme à §8 (« Ne pas dupliquer le bouton Back dans la
  barre applicative » côté Mobile).
- **`MobileNavigationCommandsAdapter.RequestBack`** appelle maintenant
  `BackHandlers.NavigateBackAsync` pour la fenêtre primaire (même sélection
  que `RequestHome`, via `SingleSurfacePresentationPolicy.SelectPrimary`), avec
  `AppBackSource.SystemNavigationBar`. Implémenté en `async void` — choix
  déjà correct pour le contrat synchrone existant
  `IMobileNavigationCommands.RequestBack() -> void`, un fire-and-forget
  déclenché par le clic du triangle système, pas une méthode dont l'appelant
  attend un résultat.
- **Écart documenté, volontairement différé** : §8 dit « Le bouton est
  désactivé lorsque `CanNavigateBack` est faux. » Cette tranche ne câble pas
  cet état — le bouton Desktop est toujours actif dès que `SupportsBack` est
  vrai, sans écouter `IAppBackHandler.CanNavigateBackChanged`. Câbler l'état
  vivant demanderait un canal de notification supplémentaire depuis le
  composant capturé vers `WindowChrome` ; à faire dans une tranche
  ultérieure si un besoin réel se présente.
- **Seule l'étape 2 de la séquence ordonnée de §7.3 est livrée.** Les étapes 1
  (pile de dialogues/modals système), 3 (pile de navigation applicative/shell)
  et 4 (fermeture-et-retour-Home) attendent `MOB-011` ; un résultat
  `NotHandled` (ou l'absence de handler) reste un no-op silencieux — c'est
  l'issue terminale valide de l'étape 5 tant que les étapes précédentes
  n'existent pas pour la prévenir.

### 16.10 Ce que « MOB-011 » a livré (2026-08-23)

Complète les étapes 1/3/4/5 de la séquence Back de §7.3 restées ouvertes
depuis §16.9, et livre la surface Recent de §7.5, sans introduire de fixture
lourde ni de nouveau moteur parallèle à `WindowRuntime`.

- **`MobileNavigationHistory`** (nouveau,
  `Platform/HackerOs.Platform.Blazor/Shell/`) — une pile de
  `Stack<WindowId>` réactive : elle s'abonne à `WindowRuntime.StateChanged`,
  recalcule `SingleSurfacePresentationPolicy.SelectPrimary` à chaque tick, et
  empile l'ancien primaire dès qu'il change tant que la fenêtre existe encore
  (fermée = jamais empilée). Ce diff réactif capture *tout* changement de
  primaire — un intent qui lance une fenêtre, une activation depuis Recent,
  n'importe quoi d'autre — sans qu'aucun site d'appel n'ait besoin de pousser
  explicitement. `TryPopPrevious` ignore silencieusement les entrées
  devenues obsolètes (fenêtre fermée depuis) et marque l'entrée retournée
  comme un retour attendu (`_pendingReturn`) pour que le
  `WindowFocusedEvent` qui suit ne soit pas ré-empilé comme une nouvelle
  navigation avant.
- **Étape 1 (dialogue/modal annulable)** : aucune pile de dialogues séparée
  n'a été construite — la fenêtre primaire elle-même porte déjà
  `Modality`/`OnRequestClose` (`WindowRuntimeState`), et
  `SingleSurfacePresentationPolicy.SelectPrimary` fait déjà remonter une
  boîte de dialogue owner-modal comme primaire pendant qu'elle est ouverte
  (elle a le z-order le plus élevé). `RequestBack` traite donc
  `Modality: OwnerModal` + `OnRequestClose` non nul comme le signal
  « annulable » et route directement vers ce délégué — aucun nouveau
  concept de pile de modals n'était nécessaire.
- **Étape 3 (retour à l'entrée précédente)** : `history.TryPopPrevious`,
  puis Restore (si minimisée) + Focus — même forme que
  `TaskbarWindowInteraction.HandleClick` pour un item minimisé.
  `SingleSurfaceArea.OnParametersSetAsync` (préexistant, non modifié)
  reminimise automatiquement l'ancienne fenêtre primaire au prochain rendu ;
  cette tranche n'a pas eu besoin de gérer elle-même la minimisation.
- **Étape 4 (fermeture gardée et retour Home)** : `RequestBack` prend un
  délégué `Func<WindowId, Task> closeWindow` plutôt que d'injecter
  directement `WindowCloseCoordinator`/`WindowCloseGuardRegistry` — **aucun
  test unitaire du dépôt ne construit ces deux services réels** (leur seul
  consommateur, `PlatformShellSwitchCoordinatorTests`, utilise une fixture
  privée de noyau complet à 12 paramètres) ; construire ça ici aurait fait
  déborder cette tranche bien au-delà de « petite et autonome ». Le vrai
  chemin gardé (`CloseCoordinator.CloseAsync(id, ct =>
  CloseGuards.ConfirmCloseAsync(id, ct))`) est injecté depuis
  `MobileShell.razor`, identique à ce que
  `TaskbarCommandDispatcherAdapter.Close` fait déjà pour la taskbar — les
  tests unitaires de l'adaptateur utilisent un simple délégué d'enregistrement
  à la place, le vrai chemin gardé restant exercé par le test navigateur
  E2E.
- **Étape 0, ajout délibéré non nommé par §7.3** : un panneau de
  notifications ouvert ou la surface Recent ouverte sont fermés en priorité
  absolue avant tout le reste — c'est manifestement le vrai sommet de la
  pile UI même si les 5 étapes littérales ne le nomment pas séparément
  (même esprit que la « tranche pragmatique » déjà assumée pour `MOB-008`
  en §16.7).
- **Surface Recent** : `MobileRecentProjection` (nouveau, statique et pur,
  même style que `SingleSurfacePresentationPolicy`) trie
  `WindowRuntime.Windows` par `ZOrder` décroissant — le compteur de focus
  monotone que `WindowRuntime` maintient déjà sert directement de signal de
  récence, sans nouveau suivi. `MobileRecentItem`/`MobileRecentSurface`
  (nouveaux, `HackerOs.MobileShell.Blazor`, miroir exact de la séparation
  contrat-générique/projection-HackerOS déjà utilisée par
  `HackerOs.Taskbar.Blazor`/`TaskbarWindowSourceAdapter`) n'affichent que
  titre + icône — **aucun mécanisme de capture de miniature n'existe nulle
  part dans le dépôt** ; la protection de preview de §7.5 est donc satisfaite
  par omission (rien de sensible n'est jamais rendu) plutôt que par un
  indicateur actif. Le filtrage par compatibilité de plateforme/activation
  vivante n'est pas non plus appliqué, avec exactement le même raisonnement
  que §16.5 pour le launcher : aucune app du catalogue actuel ne déclare de
  point d'entrée Mobile-only, et désactiver une app pendant qu'une de ses
  fenêtres reste ouverte est un cas non géré ailleurs non plus dans le dépôt.
  Activer un item ferme la surface Recent et réutilise
  `TaskbarCommandDispatcherAdapter` (construit une fois dans
  `MobileShell.razor.OnInitialized`) pour Restore/Activate/Close — zéro
  logique de fermeture dupliquée par rapport à la taskbar.
- **`RequestHome` ne vide pas l'historique** : revenir plus tard vers une
  app laisse toujours l'utilisateur remonter dans ce qu'elle avait, comme le
  fait Android.
- **Deux bugs réels trouvés par le test E2E, pas seulement latents** — aucun
  des deux n'est causé par le code ajouté dans cette tranche ; les deux
  existaient depuis les Phases 2a/2b et n'avaient jamais été détectés parce
  qu'aucun test Mobile antérieur ne prenait de capture d'écran ni ne mesurait
  de géométrie réelle (seuls le rôle/la visibilité ARIA étaient vérifiés,
  insensibles à la hauteur de boîte) :
  1. **`.mobile-shell` ne remplissait jamais l'écran.** Son `height: 100%`
     remonte par `#ecosystem-host` jusqu'à `ThemeScope`, qui se rend en
     `display: contents` — une valeur qui ne propage pas fiablement une base
     de hauteur en pourcentage aux descendants selon les moteurs de rendu.
     Résultat mesuré en E2E : `.mobile-shell` ne faisait que 61px de haut (la
     hauteur naturelle de la barre système), avec `.mobile-shell-workspace`
     (`flex: 1`) écrasé à 0 — rien sous la barre système n'était visible ni
     atteignable, la barre elle-même se retrouvant collée en haut de l'écran
     faute d'espace pour le contenu au-dessus. Corrigé en ancrant
     `.mobile-shell` directement au viewport (`position: fixed; inset: 0;`)
     au lieu de `width/height: 100%` — même mécanisme que les backdrops
     shade/Recent, qui contourne entièrement la chaîne de pourcentage.
  2. **`.mobile-shade-backdrop` (`position: fixed`, `z-index: 1100`,
     introduit en Phase 2b) recouvrait physiquement `MobileSystemNavigationBar`**,
     qui n'avait aucun `z-index` propre — une fois le bug 1 corrigé et la
     barre système réellement en bas de l'écran, Playwright a échoué avec
     « intercepts pointer events » en essayant de cliquer Back pendant que le
     volet était ouvert. Concrètement, l'étape 0 de cette tranche (Back
     ferme l'overlay en priorité) était inatteignable : impossible de
     cliquer Back tant qu'un overlay couvrait la barre. Corrigé en donnant à
     `.mobile-nav-bar` `position: relative` et `z-index: 1200` (au-dessus des
     backdrops shade/Recent, tous deux à 1100) — la barre système reste
     maintenant cliquable au-dessus de n'importe quel panneau, comme sur
     Android réel.

  Les deux bugs se masquaient l'un l'autre : tant que le bug 1 n'était pas
  corrigé, la barre système et le volet occupaient la même bande de 61px en
  haut de l'écran (le bug 2 était déjà présent mais se manifestait
  différemment). Les trois tests E2E Mobile préexistants
  (`PlatformShellSwitchTests`, `MobileNotificationShadeSwipeTests`,
  `ClockPanelMobileToggleTests`) ont été rejoués après les deux correctifs et
  passent toujours sans régression.
- Tests : 4 nouveaux cas sur `MobileNavigationCommandsAdapterTests`
  (annulation de dialogue, retour d'historique, fermeture gardée, fermeture
  de l'overlay en priorité), `MobileNavigationHistoryTests` (5 cas :
  empilage/dépilage, entrées obsolètes, non-ré-empilage du retour) et
  `MobileRecentProjectionTests` (3 cas : tri, mapping, liste vide) — 16 tests
  au total dans `HackerOs.Platform.Blazor.Tests`, tous construits contre un
  vrai `WindowRuntime`/`AppBackHandlerRegistry`, aucun framework de mock.
  `dotnet build HackerOs.sln --configuration Release` : 0 avertissement/0
  erreur (a nécessité de régénérer `PublicAPI.Unshipped.txt` de
  `HackerOs.MobileShell.Blazor` via `dotnet format analyzers
  --diagnostics RS0016 --include-generated`, même procédure que
  `EXT-WIN-014`).
- **Constat fait en écrivant le test navigateur E2E, hors périmètre de cette
  tranche** : aucune application réelle du catalogue `HackerOs.Ecosystem` ne
  déclare de support Mobile (seul `HackerOs.Samples.PlatformApp`, projet de
  démonstration isolé non câblé dans le catalogue de build, le fait — voir
  `MOB-014`). Le launcher qui permettrait d'en lancer une est un chrome
  Desktop-only (`AppLauncher.razor`, jamais composé dans `MobileShell`), et
  `PlatformShellSwitchCoordinator.SelectWindowsNeedingRestart` arrête déjà
  toute fenêtre dont l'app ne résout aucun point d'entrée sur la plateforme
  cible — donc basculer vers Mobile avec une app Desktop ouverte la ferme
  plutôt que de la faire apparaître. Concrètement : **aujourd'hui, aucune
  fenêtre d'application réelle ne peut exister pendant que Mobile est actif**
  via le produit réel. Le scénario multi-fenêtres Back/Recent est donc
  entièrement prouvé au niveau unitaire ci-dessus contre un vrai
  `WindowRuntime` (qui ne passe pas par ce filtrage de plateforme), et non
  reproductible en E2E avec le catalogue actuel. Le test navigateur
  (`MobileRecentAndOverlayBackTests.cs`) prouve à la place ce qui est
  réellement observable aujourd'hui : l'étape 0 (fermeture prioritaire du
  volet de notifications puis de Recent), l'état vide de Recent (« No recent
  apps »), et l'absence d'erreur à l'étape 5 (no-op à Home). Ce n'est pas un
  recul du périmètre de `MOB-011` — la logique multi-fenêtres est faite et
  testée — c'est une limite pré-existante et documentée de ce que le
  catalogue d'apps permet d'observer en conditions réelles tant qu'aucune
  app ne déclare de variante Mobile (`MOB-014` ne couvre qu'un exemple de
  démonstration isolé).

### 16.11 Ce que « MOB-015 » a livré (2026-08-23) — clavier virtuel réutilisable

Le clavier virtuel du §9 est livré, mais généralisé au-delà du Terminal seul,
sur demande explicite en session : un contrôle réutilisable et un conteneur
« façon SplitContainer » qui rétrécit une région de contenu quand le clavier
se déploie en dessous, plutôt qu'un composant Terminal-only. Le Terminal est
le premier consommateur, pas le seul possible.

- **`Shared/HackerOs.AppSdk.VirtualKeyboard/` (nouveau, zéro dépendance)** —
  package RCL rejoignant le lot exportable (`Packaging.props`, PublicAPI,
  README), sans aucune référence à HackerOS ou au Terminal :
  - `VirtualKey`/`VirtualKeyPressedEvent` : contrat de touche indépendant du
    renderer, calqué sur la forme de `TerminalKeyEvent` (`Shared/HackerOs.AppSdk/
    FullScreenTerminalContracts.cs`) pour un mapping direct, mais possédé par
    ce nouveau package plutôt que par le Terminal.
  - `VirtualKeyboardOptions` : bascules par rangée (modificateurs, symboles,
    flèches), même forme que `MobileSystemNavigationBarOptions`.
  - `VirtualKeyboard.razor` : rendu 100% boutons — aucun `<input>`/
    `contenteditable` nulle part, ce qui garantit structurellement qu'il ne
    peut jamais déclencher un clavier natif. Modificateurs Shift/Ctrl/Alt
    « collants », consommés par la touche suivante — délibérément plus
    simple qu'un vrai clavier (pas de verrouillage majuscules séparé), la
    surface ne devant jamais remplir une zone de texte standard. Répétition
    bornée (`PeriodicTimer`, 40 répétitions max) pour Backspace/flèches —
    terrain neuf, aucun motif de répétition longue durée n'existait ailleurs
    dans ce dépôt.
  - `VirtualKeyboardHost.razor` : le conteneur réutilisable. Aucune détection
    `ResizeObserver`/`visualViewport` — le rétrécissement de la région de
    contenu (`flex:1`) quand le clavier apparaît est un simple reflow flexbox
    conditionnel, puisque ce clavier ne détecte pas un clavier natif : il en
    tient lieu.
- **Intégration Terminal (`Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`)**,
  pas dans le nouveau package : nouveaux `UseVirtualKeyboard` (pose
  `inputmode="none"` sur l'`<input>` de mode ligne), `OnExitRequested`
  (non lié par défaut) et `PushVirtualKey(VirtualKeyPressedEvent)`. En mode
  plein écran, route vers `TerminalFullScreenSession.TryPostKey` — le même
  point d'entrée que `HandleFullScreenKeyAsync` utilise déjà, donc `nano` et
  toute autre commande plein écran héritent du support clavier virtuel sans
  aucun changement. En mode ligne, manipule `_inputBuffer` directement en C#
  plutôt que de synthétiser un `KeyboardEvent` DOM — les navigateurs
  n'appliquent pas le comportement d'édition de texte par défaut à un
  événement clavier envoyé par programme (non « trusted »).
  - **Ctrl+D** (aucun précédent existant) : n'agit que sur une ligne vide
    (comme un vrai shell — EOF seulement en ligne vide), invoque
    `OnExitRequested` si lié. Ligne non vide : no-op.
  - **Escape** (aucun précédent en mode ligne) : vide seulement la ligne en
    cours, distinct de Ctrl+L (vide le scrollback) et Ctrl+C (annule la
    commande active) — reprend le sens déjà établi ailleurs dans ce dépôt
    (`nano`/`PromptAsync` traite déjà `TerminalKey.Escape` comme « annuler
    l'édition en cours »).
- **Câblage dans un vrai shell Mobile explicitement hors périmètre** — aucune
  app du catalogue (Terminal compris) ne déclare de support Mobile
  aujourd'hui (constat de `MOB-011`), donc il n'existe aucun Terminal Mobile
  réel à câbler. Cette tranche livre l'infrastructure réutilisable et le
  point d'intégration côté Terminal ; l'activer réellement dans un Terminal
  Mobile reste un suivi conditionné à `MOB-014` couvrant une vraie variante
  Mobile.
- **Preuve navigateur, cadrée pour éviter une fixture lourde** —
  `TerminalPanel` exige un vrai `IAppExecutionContext` ; en construire un
  faux dans le harness aurait demandé le même type de fixture complète que
  celle jugée disproportionnée pour `WindowCloseCoordinator` pendant
  `MOB-011`. Le nouveau scénario `Tests/HackerOs.BrowserHarness.Tests/
  VirtualKeyboardTerminalBrowserScenario.razor` reprend plutôt le motif déjà
  autonome de `TerminalFullScreenBrowserScenario.razor` (boucle plein écran
  auto-suffisante, sans `AppContext`), enveloppé dans `VirtualKeyboardHost`.
  Le test Playwright `Virtual_keyboard_drives_the_full_screen_terminal_without_a_native_input`
  (`Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.cs`) tape des
  lettres et Ctrl+X à l'écran, confirme que le terminal les reçoit via le
  pipeline `TerminalKeyEvent` existant, et vérifie qu'aucun `<input>`/
  `[contenteditable]` n'existe nulle part dans la page. **Non couvert par un
  test automatisé cette fois-ci** : le nouveau comportement de mode ligne de
  `TerminalPanel` (`PushVirtualKey`, Ctrl+D, Escape) — cohérent avec le fait
  que les branches existantes de `HandleKeyDownAsync` n'ont elles-mêmes
  jamais eu de couverture automatisée dans ce dépôt (pas de bUnit ici, preuve
  UI seulement via le harness navigateur).
- **Non vérifié dans cette tranche** : l'hypothèse que le pipeline existant
  `ResizeObserver` → `UpdateTerminalViewport` → `TryPostKey(Resize)` reflow
  déjà correctement une commande plein écran quand le clavier apparaît/
  disparaît en direct — le nouveau scénario affiche le clavier de façon
  statique (`IsKeyboardVisible="true"` fixe) et ne teste jamais la
  bascule. Reste à vérifier concrètement quand `MOB-016` sera attaqué, pas
  supposé acquis.
- Tests : 4 nouveaux cas unitaires (`Tests/HackerOs.AppSdk.VirtualKeyboard.Tests`)
  pour `VirtualKeyboardOptions`/`VirtualKeyPressedEvent`, plus le nouveau test
  E2E ci-dessus. `dotnet build HackerOs.sln --configuration Release` : 0
  avertissement/0 erreur (a nécessité de régénérer `PublicAPI.Unshipped.txt`
  via `dotnet format analyzers --diagnostics RS0016 --include-generated`,
  même procédure que pour `HackerOs.MobileShell.Blazor` pendant `MOB-011`).
  Les suites `HackerOs.AppSdk.Terminal.Tests` (9/9) et le test E2E plein écran
  préexistant ont été rejoués : aucune régression.

