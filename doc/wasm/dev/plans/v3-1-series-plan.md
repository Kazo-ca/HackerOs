# Série v3.1 : lacunes de la migration wasm v3

## Objet

Le bilan [`wasm-v3-final-status.md`](wasm-v3-final-status.md) laisse des
lacunes après la migration v3. Le 2026-10-17, Benoit a choisi « Série v3.1
d'abord » : de petits chantiers sur une branche `v3.1/base`, avant les suites
de l'étape 5 ([`execution-followups-plan.md`](execution-followups-plan.md)),
et le SDK passe en 1.1.

Deux lacunes de la liste de départ sont traitées ailleurs, par les fils
d'origine, dans des PR vers `v3/base` :

- les associations de fichiers et de schémas propres à chaque utilisateur
  (`~/.config/hackeros/file-associations.json`) : fil « V3 4 : intents » ;
- la commande `passwd` et la réinitialisation d'un mot de passe par un
  administrateur (Paramètres, trousseau) : fil « V3 8 : réglages sensibles ».

La série v3.1 garde donc deux chantiers de code, plus la publication de la 1.1.

## Branches

- `v3.1/base` part de `v3/base` à `ce8c33a` (fusion de la PR #168).
- Chaque chantier part de `v3.1/base`, sur une branche `v3.1/<n>-<sujet>`, et
  sa PR vise `v3.1/base`. Jamais de fusion vers `main` : Benoit décide quand
  ramener ces branches.
- Quand les PR des associations et de `passwd` sont fusionnées dans
  `v3/base`, `v3/base` est fusionnée dans `v3.1/base` (commit de fusion, pas de
  rebase) par une PR `v3.1/sync-v3-base`. C'est obligatoire avant
  `v3.1/4-sdk-1-1`, pour que leurs ajouts au SDK sortent dans la 1.1.

## Chantiers

| Chantier | Contenu | ADR | Dépend de |
|---|---|---|---|
| `v3.1/1-adr` | Ce plan et les ADR de la série | 0116, 0117 | — |
| `v3.1/2-window-geometry` | Taille et état agrandi enregistrés et restaurés, pas la position (ADR 0122, réponse de Benoit) ; `WindowOptions.RememberGeometry` — **fait**, voir [`window-geometry.md`](../window-geometry.md) | [0116](../adr/0116-window-geometry-restore.md), [0122](../adr/0122-window-size-only-not-position.md) | `v3.1/1` |
| `v3.1/3-window-title` | `WindowAppBase.SetWindowTitleAsync`, événement `WindowTitleChanged`, adoption par Hack Paint, les éditeurs et l'explorateur | [0117](../adr/0117-sdk-window-title-and-sdk-1-1.md) §1-4, [0123](../adr/0123-window-title-implementation-adjustments.md) (fait, voir [`window-model.md`](../../framework/window-model.md) §4) | `v3.1/1` |
| `v3.1/sync-v3-base` | Fusion de `v3/base` (associations par utilisateur, `passwd`) dans `v3.1/base` — **fait** (PR #175) | — | ces deux PR fusionnées dans `v3/base` |
| `v3.1/4-sdk-1-1` | `Unshipped` versé dans `Shipped`, version 1.1.0, app de compatibilité 1.0 chargée sur 1.1, vérification complète, bilan mis à jour — **fait**, voir [`sdk-packages.md`](../../framework/sdk-packages.md#sdk-11) | [0117](../adr/0117-sdk-window-title-and-sdk-1-1.md) §5 | `v3.1/2`, `v3.1/3`, `v3.1/sync-v3-base` |

- `v3.1/2` et `v3.1/3` peuvent avancer en parallèle. Ils touchent tous deux
  `WindowRuntime`, `WindowLaunchCoordinator`, `window-model.md` et les fichiers
  `PublicAPI.Unshipped.txt` : le second fusionné reprend l'autre avant.
- Numéros d'ADR réservés à la série : 0116 à 0119 ; 0118 et 0119 restent
  libres pour un ajustement de mise en œuvre (un ADR par chantier qui s'écarte
  de sa décision). L'ADR 0122 (taille seulement, sans la position) corrige
  l'ADR 0116 après la réponse de Benoit. `v3.1/3` a pris 0123, attribué par
  l'orchestration.

## Barre de fusion

GitHub Actions ne tourne pas dans le dépôt : la suite locale fait foi, comme
pour la série v3. Avant de fusionner, chaque chantier compile la solution et
passe les tests des projets qu'il touche, la suite UI E2E pour les fenêtres,
`HackerOs.SdkCompatibility.Tests` et `build/publish-trimmed.sh`.

## Tâches de ce chantier

- [x] Créer et pousser `v3.1/base` depuis `v3/base`.
- [x] Relire le bilan v3, les ADR 0044, 0095, 0098, 0103, 0114 et le code des fenêtres.
- [x] Écrire l'ADR 0116 (géométrie des fenêtres).
- [x] Écrire l'ADR 0117 (titre de fenêtre et publication du SDK 1.1).
- [x] Écrire ce plan et mettre à jour l'index et le bilan v3.
