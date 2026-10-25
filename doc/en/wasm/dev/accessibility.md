# Accessibilité

Chantier `v3/13-accessibility` ([ADR 0105](adr/0105-accessibility-preferences.md), ajusté par
l'[ADR 0112](adr/0112-accessibility-implementation-adjustments.md)). L'ancien document
[`old/accessibility.md`](old/accessibility.md) reste pour l'historique (règles de la phase 2 et
checklist manuelle, écartée par Benoit).

## Objet

Trois préférences, en fichiers comme tout réglage de HackerOS, appliquées par le thème à tout le
bureau et lues par les apps qui dessinent elles-mêmes :

- **Contraste élevé** : surfaces noires, texte blanc (21:1, et au moins 7:1 pour le texte le plus
  pâle), bordures blanches, contour de focus jaune de 3 px, par-dessus tous les thèmes.
- **Taille du texte** : 100 %, 125 %, 150 % ou 200 %.
- **Réduire les animations** : transitions, animations, curseurs clignotants et défilement animé
  coupés.

## Réglages

`accessibility.config`, en couches ([`settings-files.md`](../framework/settings-files.md)) :
`/etc/hackeros/accessibility.config` (défaut de la machine, écrit au premier démarrage en
commentaires, aussi lu par l'écran de connexion) puis `~/.config/hackeros/accessibility.config`.

| Clé | Valeurs | Défaut | `auto` suit |
|---|---|---|---|
| `highContrast` | `auto`, `on`, `off` | `auto` | `prefers-contrast: more` ou `forced-colors: active` de l'hôte |
| `textScale` | `1.0`, `1.25`, `1.5`, `2.0` | `1.0` | — |
| `reducedMotion` | `auto`, `on`, `off` | `auto` | `prefers-reduced-motion: reduce` de l'hôte |

`animationsEnabled = false` dans `appearance.config` compte comme `reducedMotion = on`. Une valeur
invalide est ignorée (la couche suivante s'applique). Un administrateur peut verrouiller une clé
(`[locked]`).

```sh
nano ~/.config/hackeros/accessibility.config      # highContrast=on
sudo nano /etc/hackeros/accessibility.config      # défaut de la machine
```

**Paramètres > Accessibilité** montre l'état appliqué et écrit la couche utilisateur : trois
groupes de boutons radio (contraste, taille, animations), chacun avec l'origine de sa valeur. Le
réglage « Animations » de la page Apparence a disparu : « Réduire les animations » le remplace
(choisir Auto ou Désactivé remet `animationsEnabled = true` chez l'utilisateur).

## Architecture

| Pièce | Projet | Rôle |
|---|---|---|
| `AccessibilitySettingsDocuments` | Platform.Core | Schéma du document, `AccessibilitySettings`, `HostAccessibilitySignals`, `Resolve` (règles de `auto`). |
| `AccessibilityPreferenceService` | Platform.Core | Lit et surveille le document, suit `ThemePreferenceService` (pour `animationsEnabled`) et la sonde de l'hôte ; `Effective` et `Changed`. Un par session. |
| `IHostAccessibilityProbe`, `BrowserHostAccessibilityProbe`, `hostAccessibilityProbe.js` | Platform.Core, Platform.Blazor | Requêtes média de l'hôte, sans décision. |
| `AppAccessibilityGateway` | Platform.Core | La vue d'une app, branchée par `AppExecutionContextFactory`. |
| `HackAccessibility`, `HackAccessibilityPreferences`, `IAppAccessibilityGateway` | AppSdk | API publique. |
| `ThemeScope` (`HighContrast`, `TextScale`) | Theming.Blazor | Attributs `data-contrast` et `data-text-scale` à côté de `data-motion`. |
| `themes.css` | Theming.Blazor | Couche de contraste élevé, police racine, règle de mouvement réduit. |
| `AccessibilityPanel` | Apps.Settings | La page Accessibilité. |

`App.razor` de l'hôte initialise le service après l'apparence et passe à `ThemeScope`
`AnimationsEnabled = !ReducedMotion`, `HighContrast` et `TextScale`.

### Dans le thème

- **Contraste élevé** : `[data-contrast="high"]` redéfinit tous les jetons de couleur, de bordure,
  d'ombre et de focus en `!important`, sur la racine et sur les `[data-theme]` imbriqués. Un thème
  utilisateur ne peut pas l'enlever. `:focus-visible` reçoit un contour jaune de 3 px.
- **Taille du texte** : les tailles de texte sont en `rem` ;
  `:root:has(.theme-scope[data-text-scale="150"])` met la police racine à 150 %. Les cadres en
  `px` ne bougent pas. `--hos-text-scale` vaut le facteur.
- **Mouvement réduit** : `data-motion="reduced"` met les jetons de transition à 0 et ramène toute
  durée d'animation ou de transition à 0,01 ms.

Nouvelle feuille : écrire les tailles de texte en `rem`, jamais en `px`, et les couleurs avec les
jetons `--hos-*`.

### Apps qui dessinent elles-mêmes

| App | Contraste élevé | Taille du texte | Mouvement réduit |
|---|---|---|---|
| Terminal (xterm.js, [`terminal-emulator.md`](terminal-emulator.md)) | Palette ANSI à 7:1, fond noir, curseur et sélection jaunes | Police relue du panneau (en `rem`) | Curseur fixe |
| Code Editor (Monaco) | Thème `hackeros-high-contrast` (base `hc-black`) | Police et hauteur de ligne × facteur | Curseur fixe, défilement sans animation |
| Hack Paint | Cadre et grille visibles autour de l'image (l'image garde ses couleurs) | — (interface en `rem`) | Indicateur « occupé » immobile |

Chacune lit `AppContext.Accessibility` à l'ouverture et suit `Changed`.

## API

```csharp
using HackerOs.AppSdk.Accessibility;

HackAccessibilityPreferences a11y = await HackAccessibility.GetPreferencesAsync();
int fontSize = a11y.Scale(14);                 // 28 à 200 %
bool blink = !a11y.ReducedMotion;
HackAccessibility.PreferencesChanged += (_, _) => { /* relire et redessiner */ };
```

Aucune capacité. Sans service (tests, processus d'un Web Worker), les valeurs par défaut.

## Tests

- Unitaires : `AccessibilityPreferenceServiceTests` (Platform.Core : règles de `auto`, couches,
  valeur invalide, hôte, `animationsEnabled`), `HackAccessibilityTests` (AppSdk),
  `ThemeComponentRenderTests` (attributs de `ThemeScope`).
- E2E (`Tests/HackerOs.UI.E2E.Tests/AccessibilityPreferencesTests.cs`, voir
  [`building-and-testing.md`](building-and-testing.md)) :
  - scans axe du Terminal, du navigateur, de la calculatrice, du moniteur système, de Theme
    Editor, de Paramètres > Sauvegarde et de Paramètres > Accessibilité, avec et sans contraste
    élevé (activé par la page Accessibilité) ; une violation sérieuse ou critique fait échouer ;
  - parcours au clavier seul : Tab jusqu'au lanceur, Entrée, recherche, flèche, Entrée ; deux
    fois ; `` Alt+` `` ; `Alt+F7` puis flèches et Entrée ; `Alt+Q` ; focus visible à chaque étape ;
  - `textScale = 2.0` : police racine à 32 px, aucune barre de défilement horizontale ni fenêtre
    hors de l'écran pour le bureau, l'explorateur, Paramètres, le Terminal, la calculatrice, le
    moniteur système et l'éditeur de texte.
- Les scans existants (`AccessibilityAndVisualCoverageTests`) restent.
- Défauts trouvés par les premiers scans et corrigés : la barre d'onglets du Terminal annonçait
  une liste d'onglets qui contenait aussi les boutons « Fermer » et « + » (c'est maintenant un
  groupe de boutons, l'onglet actif porte `aria-current`) ; le tableau des processus du moniteur
  système déclarait `aria-multiselectable` sans être une grille (`role="grid"` ajouté). Le
  parcours au clavier a montré qu'après `` Alt+` `` le focus clavier restait sur `<body>` :
  la fenêtre activée le reçoit maintenant ([`window-model.md`](../framework/window-model.md)).

## Décisions

Voir l'[ADR 0112](adr/0112-accessibility-implementation-adjustments.md) : police racine par
`:has()`, couche `!important` qui couvre aussi les aperçus, règle globale de mouvement réduit,
page qui écrit la couche utilisateur seulement, requêtes média décidées en C#, valeurs par défaut
hors du fil principal.

## Tâches

- [x] Document `accessibility.config` (schéma, fichier `/etc` par défaut, registre des documents de l'OS).
- [x] `AccessibilityPreferenceService` et sonde des requêtes média de l'hôte.
- [x] `HackAccessibility` dans le SDK, passerelle branchée par la fabrique de contextes.
- [x] `ThemeScope` : `data-contrast`, `data-text-scale` ; mouvement réduit effectif.
- [x] `themes.css` : couche de contraste élevé, police racine, règle de mouvement réduit ; tailles de texte en `rem`.
- [x] Paramètres > Accessibilité (anglais et français) ; réglage Animations retiré d'Apparence.
- [x] Terminal, Code Editor et Hack Paint suivent les préférences.
- [x] Tests unitaires, scans axe avec et sans contraste élevé, parcours clavier, test à 200 %.
- [x] Documentation, ADR 0112 et index.
