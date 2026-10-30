> [English version](../../../en/wasm/memory/known-test-failures.md)

# Échecs de tests préexistants connus

État au **2026-10-30**. Ces tests échouent ou sont instables sur `main` sans
lien avec une PR. Une PR qui les voit échouer les **nomme** dans sa section
Tests ; elle ne les désactive pas, ne les ignore pas et ne les met pas en
quarantaine. Si un de ces tests passe de nouveau de façon stable, le retirer de
cette liste. Les causes déjà trouvées et corrigées sont dans
[`../dev/pitfalls.md`](../dev/pitfalls.md) §4.

## Échouent à chaque fois (conteneur cloud Linux)

| Suite | Test | Symptôme |
| --- | --- | --- |
| `HackerOs.E2E.Tests` | `Window_runtime_renders_and_handles_mouse_and_touch_pointer_gestures`, `Window_runtime_handles_every_resize_edge_in_real_browser` | Délai dépassé ; toujours là après l'ADR 0177. |
| `HackerOs.UI.E2E.Tests` | `Every_listed_surface_has_no_serious_axe_violation…`, `Representative_real_app_surfaces…` (axe) | `label-content-name-mismatch` sur `.launcher-trigger` et `.taskbar-clock` de la barre des tâches. |
| `HackerOs.TrimmedPublish.E2E.Tests` | test de la Calculatrice (axe) | `label-content-name-mismatch` sur les touches MC, MR, M+, M-, AC, x². |
| `HackerOs.UI.E2E.Tests` | `LowStorageSpaceTests.A_full_quota_warns_once…` | « No low-space notification within 60 s ». |

## Instables (passent seuls, échouent parfois sous charge)

- `AppInstallationService` (tests unitaires) ;
- `LocalNetworkTests` avec un web worker ;
- le harnais E2E des fenêtres détachées ;
- `HackerOs.UI.E2E.Tests` : `FileExplorerDownload` et `BrowserHomePage` quand
  toute la suite tourne ;
- `HackerOs.Platform.WorkerHost.Tests` : un test de temps.

La CI relance une fois un test en échec et signale « Flaky test » s'il passe au
second essai ([`../dev/ci-workflows.md`](../dev/ci-workflows.md)).

## Astuces d'environnement récentes

Elles complètent [`../dev/pitfalls.md`](../dev/pitfalls.md) et
[`../dev/building-and-testing.md`](../dev/building-and-testing.md).

- Le SDK `/usr/bin/dotnet` des conteneurs (10.0.114 au 2026-10-30) compile
  encore mal `ResponseBodyViewer.razor`. Le SDK Microsoft 10.0.403 dans
  `~/.dotnet`, en tête du `PATH` avec `DOTNET_ROOT`, fonctionne.
- Si le bac à sable refuse `curl … dotnet-install.sh | bash`, lire l'URL de
  l'archive du SDK et son `sha512` dans
  `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`,
  vérifier l'empreinte, puis décompresser dans `~/.dotnet`.
- Dans un conteneur, Chromium sans interface plante en relisant un handle OPFS
  depuis IndexedDB : un test Playwright de reconnexion d'un lecteur réseau
  (ADR 0181) n'y est pas possible.
- Pour reproduire un test de temps instable, charger le CPU
  (`for j in 1 2 3 4; do (timeout 300 sh -c 'while :; do :; done' &); done`)
  puis relancer le test en boucle avec `dotnet test --no-build --filter …`.
- Accueil mobile : il y a deux boutons Paramètres (`.mobile-home-screen` et le
  dock) ; dans Playwright, prendre `.first()`.
