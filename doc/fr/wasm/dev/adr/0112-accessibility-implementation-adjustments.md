# ADR 0112 : ajustements de la mise en œuvre de l'accessibilité

## Statut

Accepté le 2026-10-17 (chantier `v3/13-accessibility`).

- **Complète :** [ADR 0105](0105-accessibility-preferences.md). Précise ses §1, §2, §3 et §4 ;
  ne change aucune de ses décisions.
- Numéro : `0112`, réservé pour ce chantier.

## Contexte

La mise en œuvre de l'ADR 0105 a rencontré six points que l'ADR ne tranche pas.

## Décision

1. **Taille du texte par la police racine.** `ThemeScope` est un élément `display: contents`
   au milieu de la page, alors qu'une taille en `rem` se mesure sur `<html>`. `themes.css`
   règle donc la police de la racine avec `:root:has(.theme-scope[data-text-scale="200"])`
   (`font-size: 200%`), sans JavaScript. Les 164 tailles de texte en `px` des feuilles de la
   plateforme, des apps système et des bibliothèques du SDK sont passées en `rem` (valeur ÷ 16,
   identique à 100 %). Les cadres, bordures et marges en `px` ne bougent pas : seul le texte
   grandit. Les exemples (`Apps/Samples`) et les pages web gardent leurs tailles.
   `--hos-text-scale` (1 à 2) reste exposé pour les composants qui dimensionnent par script.
2. **Contraste élevé `!important`, aperçus compris.** Les jetons d'un thème utilisateur sont
   posés en style en ligne sur la racine du thème (ADR 0103) ; seule une déclaration
   `!important` d'une feuille passe devant. La couche s'applique aussi aux éléments
   `[data-theme]` imbriqués (aperçus de Paramètres, Theme Editor) : en contraste élevé, les
   aperçus montrent la couche, comme sous le contraste élevé de Windows.
3. **Mouvement réduit au-delà des jetons.** `data-motion="reduced"` mettait déjà les jetons de
   transition à 0. Des composants déclarent des durées en dur (animation « occupé » de Hack
   Paint, ouverture de fenêtre) : une règle globale les ramène à 0,01 ms, ce qui garde les
   événements `animationend`/`transitionend` dont du code dépend. `ThemeScope` reçoit
   `AnimationsEnabled = !ReducedMotion` : `animationsEnabled = false` et `reducedMotion = on`
   donnent le même rendu.
4. **Paramètres écrit la couche utilisateur seulement.** La page Accessibilité écrit
   `~/.config/hackeros/accessibility.config`. Le défaut de la machine (`/etc`, aussi lu par
   l'écran de connexion) s'édite avec `sudo nano`, comme `/etc/locale.conf` pour la langue.
   « Réduire les animations » y est un choix à trois valeurs (Auto, Activé, Désactivé) comme le
   contraste. Choisir Auto ou Désactivé remet aussi `animationsEnabled = true` dans la couche
   utilisateur d'`appearance.config`, sinon l'ancien interrupteur garderait les animations
   coupées ; un verrou de l'administrateur reste, et la page le signale.
5. **Préférences de l'hôte par requêtes média.** `auto` suit `prefers-contrast: more`,
   `forced-colors: active` et `prefers-reduced-motion: reduce`, lus par un petit module
   (`hostAccessibilityProbe.js`, Platform.Blazor) qui ne décide rien ; la résolution est en C#
   (`AccessibilitySettingsDocuments.Resolve`). Un hôte sans navigateur répond « rien demandé ».
6. **Processus sans interface.** `IAppExecutionContext.Accessibility` a un membre par défaut
   qui répond les valeurs par défaut (`DefaultAppAccessibilityGateway`). Un processus d'un Web
   Worker (ADR 0070) n'a pas de service et le reçoit : il ne dessine rien. Lire les préférences
   ne demande aucune capacité.

## Conséquences

- Une app qui dessine elle-même lit `HackAccessibility`. Le Terminal (xterm.js), le Code Editor
  (Monaco) et Hack Paint le font ; leurs paquets JavaScript sont reconstruits (`npm run build`),
  les sorties restent identiques à la source.
- Un composant qui écrit une taille de texte en `px` ne suit plus la taille du texte : les
  nouvelles feuilles utilisent `rem`.
- Les tests de l'ADR 0105 §4 sont dans `AccessibilityPreferencesTests` (UI.E2E) ; voir
  [`accessibility.md`](../accessibility.md).
