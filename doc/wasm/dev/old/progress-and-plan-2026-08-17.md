# État d'avancement et plan d'action — 2026-08-17

## Objet

Photo de l'avancement de la migration HackerOS v3 à cette date, et proposition
de plan d'action priorisé. Ce document ne remplace pas
[`integration-task-list.md`](integration-task-list.md) (source de vérité pour
le détail tâche par tâche) ni [`server-implementation-pass.md`](server-implementation-pass.md)
(source de vérité pour le serveur/proxy) — il sert de vue de synthèse et de
séquencement pour décider quoi attaquer ensuite.

## 1. Résumé exécutif

| Priorité | Chantier | Pourquoi maintenant |
|---|---|---|
| ~~1~~ | ~~Fermer le gate de sortie Phase 2~~ | **Presque fait le 2026-08-17** — voir section 4 ; ne reste que la facturation GitHub, hors code (la checklist humaine de `P2-GATE-003` a été explicitement marquée skip) |
| 2 | Continuer Phase 5 (serveur/proxy) | En cours dans une autre session ; suite logique déjà identifiée dans `server-implementation-pass.md` |
| ~~3~~ | ~~Clore réellement l'extraction fenêtres/taskbar~~ | **Fait le 2026-08-17** — voir section 2 |
| 4 | Plateforme Mobile | Suite logique de l'extraction, maintenant close (le doc mobile n'a aucune tâche cochée) |
| 5 | Finir Wave 3 / Wave 5 (hors réseau simulé/gameplay) | Reste du travail de migration fonctionnelle, priorisé avant le contenu réseau/gameplay |
| Repoussé | Expansion réseau simulé & contenu gameplay | Le cœur technique (Wave 4, Wave 6) est déjà fait ; seule l'expansion de contenu reste, et elle est volontairement mise de côté |

## 2. Extraction fenêtres/taskbar (`window-taskbar-export-plan.md`) — CLOSE 2026-08-17

**Déjà confirmé fait** (PR #79 mergée — branche `feature/windowing-taskbar-extraction`,
historique `EXT-WIN-001` à `EXT-WIN-015`) :

- `HackerOs.Windowing.Core` (moteur headless, zéro dépendance HackerOS) — existe.
- `HackerOs.Windowing.Blazor` (RCL fenêtres/chrome) — existe.
- `HackerOs.Taskbar.Blazor` (RCL taskbar par contrats) — existe.
- `Samples/HackerOs.Windowing.SampleHost` — existe, ne référence pas `HackerOs.Ecosystem`.
- Adaptateurs HackerOS (`Shell/TaskbarAdapters.cs`) et migration de `DesktopShell` — faits, vérifiés navigateur.
- Packaging NuGet local + baseline API publique + preuve Release/trimming — faits.
- Couverture de tests (headless, composants, E2E Playwright `taskbar`) — faite.

**Les deux points restés ouverts malgré la mention "extraction terminée le
2026-08-13" ont été fermés aujourd'hui :**

- [x] **`EXT-WIN-001`** — Nouveau projet `Platform/HackerOs.Windowing.Abstractions/`
  créé : `WindowId`, `WindowOwnerId`, `WindowBounds`, `WindowConstraints`,
  `WindowVisualState`, `WindowModality`, `WindowRuntimeState`, et toutes les
  commandes/événements y sont déplacés (même noms, nouveau namespace).
  `HackerOs.Windowing.Core` ne garde que la classe `WindowRuntime` et
  référence Abstractions. Ajouté à `HackerOs.sln` via `dotnet sln add`. Les
  30 fichiers consommateurs mis à jour. `dotnet build` (Debug et Release) et
  `dotnet test` passent tous à 0 échec ; `dotnet pack`/`dotnet publish`
  produisent les 4 packages/assemblys exportés sans avertissement.
- [x] **`WindowSurface`** — Composant implémenté dans `HackerOs.Windowing.Blazor`
  (`WindowSurface.razor`) : reçoit `Runtime` + `WindowContent`, s'abonne à
  `Runtime.StateChanged`, et traduit les callbacks de `DesktopArea` en
  `Runtime.Apply(...)`. Le sample host est migré vers
  `<WindowSurface Runtime="WindowRuntime">`, éliminant le boilerplate qu'il
  réimplémentait à la main. Vérifié au navigateur : ouverture, focus,
  interaction, maximize/restore, minimize/restore par la taskbar, close —
  sans erreur console. `DesktopShell` (l'hôte HackerOS complet) reste
  délibérément sur son chemin actuel via `TaskbarAdapters` : il gère aussi
  les dialogues et la fermeture avec garde/confirmation, deux préoccupations
  hors du périmètre minimal de `WindowSurface` — migrer y introduirait une
  rupture sans bénéfice réel.

**Point relevé pendant la vérification, hors scope de ce lot** : la suite
`HackerOs.E2E.Tests` a un test préexistant flaky/racy sans rapport avec le
windowing — `IndexedDbBrowserContractTests.Group_and_settings_contracts_pass_in_real_browser`
échoue de façon reproductible avec `IndexedDB database 'hackeros' version 2 is
not open`, une race entre `VerifyBackupRestoreAsync` (restore destructif) et
`VerifyOrphanCleanupAsync` (appel JS brut juste après) dans
`Tests/HackerOs.BrowserHarness.Tests/App.razor`. Reproduit de façon identique
avant et après ce lot de travail ; n'affecte aucun test lié aux
fenêtres/taskbar. Laissé pour un futur passage sur le sujet IndexedDB backup/
recovery plutôt que traité ici.

## 3. Plateforme Mobile (`mobile-interface-platform-plan.md`) — Phases 0, 1 et 2 faites, 2026-08-18

`MOB-001`, `MOB-006` (tranche persistance), `MOB-007` (Phase 0), puis
`MOB-002`, `MOB-003`, `MOB-004`, `MOB-005`, `MOB-014` (partiel, Phase 1), puis
`MOB-008`, `MOB-009`, `MOB-010`, `MOB-012`, `MOB-013` (Phase 2, complète et
sa sous-tranche 2c) sont faits — voir `mobile-interface-platform-plan.md`
§14/§16 pour le détail fichier par fichier.

Phase 0 : le taskbar expose un point d'ancrage générique
(`ITaskbarClockPanelSource` + `RenderFragment ClockPanelContent`) ouvert en
cliquant l'horloge, où vit le sélecteur Auto/Desktop/Mobile. Le toggle persiste
la préférence mais ne change pas encore le rendu du shell (`MOB-008`/`009`,
Phase 2).

Phase 1 : le manifeste supporte désormais plusieurs points d'entrée par
plateforme (`AppManifest.Platform`, mutuellement exclusif avec l'ancien
`EntryPoint`) sans qu'aucun des `app.manifest.json` existants n'ait dû être
modifié (un manifeste `entryPoint`-only est traité comme Desktop-only par
normalisation, pas par migration physique). `IAppPlatformEntryPointResolver`
résout l'entrée effective par plateforme et est câblé dans
`AppEntryPointDiscovery` (défaut `desktop`, donc comportement inchangé pour le
shell Desktop actuel). Le filtrage des intents/associations/launcher sur la
préférence de plateforme *active* est volontairement reporté à `MOB-008`
(Phase 2) — l'appliquer maintenant masquerait toutes les apps existantes dès
qu'un utilisateur bascule le toggle Mobile de Phase 0, sans shell Mobile pour
les reprendre. `MOB-008` à `MOB-018` restent à faire, reséquencés en phases
dans `mobile-interface-platform-plan.md` §16.4/§16.5.

Phase 2a (sous-tranche, après le spike de faisabilité §16.2) : le moteur de
fenêtrage gagne un mode de présentation « surface unique plein écran »
réutilisant les mécanismes Maximize/Minimize existants
(`SingleSurfacePresentationPolicy`, `WindowHost.ShowChrome`,
`SingleSurfaceArea`), et un nouveau package `HackerOs.MobileShell.Blazor`
(frère de `HackerOs.Taskbar.Blazor`) fournit `MobileSystemNavigationBar`
(Back/Home/Recent). En bonus, un vrai bug latent a été corrigé au passage :
`WindowChrome` ignorait déjà `Constraints.IsResizable` (les dialogues de
fichiers non redimensionnables en étaient déjà affectés).

Phase 2b (sous-tranche, `MOB-008`) : `PlatformShellSwitchCoordinator` confirme
puis arrête (`ProcessExitReason.PlatformChanged`, nouveau) seulement les
fenêtres dont le point d'entrée change réellement entre les deux plateformes
(une fenêtre à point d'entrée partagé, ex. `HackerOs.Samples.PlatformApp`,
survit telle quelle grâce au `WindowRuntime` singleton partagé entre les deux
shells) — puis persiste le choix, dans cet ordre, contrairement au câblage
Phase 0 de `ClockPanel.razor` qui persistait sans confirmer. `App.razor` rend
maintenant `MobileShell` ou `DesktopShell` selon la préférence active, en
direct et sans reload — prouvé par un vrai test navigateur
(`PlatformShellSwitchTests.cs`). Un volet de notifications accessible par
glissement depuis le haut de l'écran Mobile (`MobileShell.razor.js`,
`attachSwipeDownGesture`) réutilise `ClockPanel` (donc le sélecteur
Auto/Desktop/Mobile) — remplace un premier bouton placeholder. Le
filtrage launcher/intents/associations par plateforme (§6.3 étape 8) reste
différé : aucun manifeste embarqué ne déclare de point d'entrée Mobile-only,
donc rien n'est aujourd'hui incorrect à laisser non filtré.

Phase 2c (sous-tranche, `MOB-012`/`MOB-013`) : le contrat SDK
`IAppBackHandler` (`AppBackContracts.cs`) existe, `AppManifest.SupportsBack`
déclare le support par point d'entrée, `AppEntryPointDiscovery` valide par
réflexion (même motif que `DerivesFrom`) que le type déclaré implémente bien
le contrat, et `AppBackHandlerRegistry` capture le composant vivant comme
`WindowCloseGuardRegistry` le fait déjà pour la fermeture. `WindowChrome`
affiche un bouton Back Desktop-only quand `Window.SupportsBack`, Mobile
route son triangle système vers le même registre
(`MobileNavigationCommandsAdapter.RequestBack`). Seule l'étape 2 de la
séquence Back ordonnée de §7.3 est couverte (le handler applicatif) ; les
étapes 1/3/4 (pile de dialogues, pile de navigation, fermeture-et-Home)
attendent `MOB-011`. Bug latent réel trouvé par le nouveau test :
`WindowRuntime.Copy()` oubliait de reporter `SupportsBack` lors de la
création d'une fenêtre. `MOB-011`, `MOB-015` à `MOB-018` restent à faire.

Le document d'extraction fenêtres/taskbar référence déjà ce plan comme risque
à éviter ("Rupture mobile : ne pas figer dans le moteur des hypothèses
propres aux fenêtres flottantes Desktop"), donc la séquence
extraction → mobile est cohérente avec ce qui est déjà écrit, même si ce
n'est pas formulé comme un prérequis strict dans le document mobile
lui-même.

**Recommandation** : la section 2 ci-dessus est maintenant close — Mobile est
prêt à démarrer comme prochain chantier de fonctionnalité (après Phase 2 gate
et en parallèle ou après la suite serveur selon la bande passante disponible).

## 4. Gate de sortie Phase 2 (priorité 1) — PRESQUE FERMÉ 2026-08-17

Voir [`integration-task-list.md#22-phase-2-acceptance-and-exit-gate`](integration-task-list.md)
et [`phase-2-acceptance.md`](phase-2-acceptance.md) pour le détail complet des
preuves. État à l'issue de la session du 2026-08-17 :

- [x] `P2-ACC-015` — PWA publiée fonctionne après install en ligne, serveur
  arrêté, navigateur hors-ligne. **Preuve réelle** :
  `Tests/HackerOs.Pwa.E2E.Tests` (nouveau projet), contre un vrai
  `dotnet publish` servant le vrai `service-worker.published.js`.
- [x] `P2-ACC-016` — Une mise à jour PWA préserve les données compatibles et
  ne mélange jamais les assets de deux versions. **Preuve réelle**, même
  projet.
- [ ] `P2-ACC-017` — Matrice CI navigateur réelle verte et hébergée.
  `HackerOs.Pwa.E2E.Tests` tourne déjà automatiquement dans le step CI
  existant (aucun câblage supplémentaire nécessaire) et les runs locaux sont
  verts. **Reste bloqué** : le compte GitHub est verrouillé pour facturation
  (`gh run view` : "the job was not started because your account is locked
  due to a billing issue") — action utilisateur, pas de code.
- [x] `P2-GATE-002` — Publication Release sans erreur de trimming/asset/
  console/réseau inexpliquée. **Deux vrais bugs trouvés et corrigés** en
  construisant cette preuve : la PWA standalone ne montait plus jamais rien
  (`RootComponents.Add<App>` était commenté dans `Program.cs`) et un lien CSS
  404ait systématiquement.
- [x] `P2-GATE-003` — **SKIPPED (partie manuelle), décision utilisateur
  explicite du 2026-08-17** — voir plan détaillé ci-dessous. Captures
  desktop/mobile + vérifications a11y. Preuve **automatisée** réelle et verte
  (nouveau test `AccessibilityAndVisualCoverageTests`, 8 catégories de
  violations réelles trouvées et corrigées sur 6 surfaces — dont une
  régression `aria-labelledby` sur chaque fenêtre, et la découverte que
  `test/test` et `HackerOs.Server` ne chargeaient jamais le vrai thème sombre
  de l'app). La **checklist humaine** clavier/lecteur d'écran
  ([`Human-test-needed-p2-GATE-003.md`](Human-test-needed-p2-GATE-003.md)) est
  **explicitement passée en skip pour ne plus bloquer la fermeture du gate** —
  ce n'est pas un abandon silencieux, c'est une décision prise en chat par
  l'utilisateur. **Plan pour plus tard** : exécuter la checklist quand la
  bande passante le permet, idéalement avant le gel de la surface SDK en
  Phase 3, et consigner le résultat selon la convention de case datée déjà en
  place dans `docs/accessibility.md` §2.4.
- [x] `P2-GATE-004` — `phase-2-acceptance.md` réécrit : chaque ligne pointe
  vers un test réel ou un écart explicitement documenté (facturation CI,
  checklist humaine skip). Plus aucune preuve par fichier d'implémentation.
- [ ] `P2-GATE-005` — Approbation utilisateur explicite. **Volontairement pas
  demandée** avant que la facturation CI soit réglée (la checklist humaine
  n'est plus un blocage, voir `P2-GATE-003` ci-dessus), conformément à
  `integration-audit-remediation.md` ("cannot be inferred from source code or
  a self-declared status line").

`P2-GATE-001` (build + tests) reconfirmé propre le 2026-08-17 après ce lot.
Un échec pré-existant sans rapport (`Group_and_settings_contracts_pass_in_real_browser`,
IndexedDB) a été retrouvé et documenté séparément (section 2 ci-dessus et
tâche de suivi dédiée) — reproductible avant et après ce lot de travail.

**Il ne reste plus qu'une seule chose, hors de portée du code** : régler la
facturation GitHub (`P2-ACC-017`). La checklist humaine de `P2-GATE-003` a
été explicitement marquée skip (voir ci-dessus) plutôt que de rester un
blocage. Une fois la facturation réglée, `P2-GATE-005` peut être demandé.

## 5. Phase 5 — Serveur/Proxy (en cours ailleurs, priorité 2)

Détail complet et à jour dans
[`server-implementation-pass.md`](server-implementation-pass.md). Prochains
items par ordre de proximité, tels qu'identifiés dans ce document :

1. `cat` sans capacité de lecture d'URL — jamais construite, nécessite sa
   propre décision de scope.
2. `curl -d` (POST réel) — hors scope actuel.
3. Pont Grants durable → application en mémoire (`LocalSessionService`) —
   changement plus large, nécessite son propre design.
4. Idempotence du push de sync au redémarrage, preuve de résolution de
   conflits par domaine, tombstones de suppression FileSystem.
5. Suite de sécurité proxy incomplète (SSRF/redirect/rebinding, quotas,
   politique de bande passante).
6. Pass N+5 (injection directe de services pour l'hôte server-hosted) et
   Pass N+6 (multi-tenant concurrent) — différés par ADR 0027, pas des oublis.

Ce chantier continue dans l'autre session ; pas d'action ici sauf coordination
de priorité globale.

## 6. Wave 3 / Wave 5 — compléter avant le contenu réseau/gameplay

Voir [`integration-task-list.md#28-wave-3-editing-clipboard-and-dragdrop`](integration-task-list.md)
et [`#30-wave-5-remaining-utility-apps-and-commands`](integration-task-list.md).

- [ ] `P4-W3-002` / `P4-W3-007` — Code Editor : preuve de rechargement rendu
  réel et validation offline/VFS complète manquantes.
- [ ] `P4-W5-APP-002` — Hack Paint : fichiers VFS image, dialogues
  import/export, couverture tactile/pixel E2E complète manquantes.

## 7. Repoussé délibérément : réseau simulé & gameplay (Phase 4, Waves 4 et 6)

Le cœur technique de la Wave 4 (réseau simulé, Browser app, `curl`/`ping`/
`nmap`, sites simulés) et de la Wave 6 (domaines gameplay) est déjà marqué
complet dans `implementation-status.md`. Ce qui resterait à faire dans cette
direction est de l'**expansion de contenu**, pas de la plomberie manquante —
notamment le pack de contenu "Game domain" qu'ADR 0023 scope séparément du
`SmokeTestNetworkSeed` minimal actuel (voir la section "Open questions" de
`server-implementation-pass.md`). Conformément à la décision prise ici, ce
travail est repoussé après les priorités 1 à 5 ci-dessus.

Dans la même veine d'idées non engagées : la compilation/exécution hybride de
code C# utilisateur (concevoir une app pour l'écosystème et l'exécuter dans le
navigateur et/ou côté serveur, sans dupliquer le compilateur) a été explorée
en session le 2026-08-17 et documentée dans
[`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md)
(référencée aussi comme suggestion `S-007` dans `integration-task-list.md`).
Aucune décision `D-xxx` n'a été prise ; ce n'est pas priorisé dans la séquence
ci-dessus.

## 8. Séquence proposée

```text
1. Fermer le gate Phase 2 (section 4)                    — court terme
2. Continuer Phase 5 serveur/proxy (section 5)            — en parallèle, autre session
✓  Extraction fenêtres/taskbar (section 2)                — FAIT le 2026-08-17
3. Démarrer la plateforme Mobile (section 3)              — prêt à démarrer
4. Finir Wave 3 / Wave 5 hors réseau (section 6)           — en parallèle de 1-3
—  Expansion réseau simulé / contenu gameplay (section 7) — repoussé
```

## 9. Documents de référence

- [`implementation-status.md`](implementation-status.md) — état implémenté condensé
- [`integration-task-list.md`](integration-task-list.md) — liste de tâches exhaustive, source de vérité
- [`server-implementation-pass.md`](server-implementation-pass.md) — roadmap serveur/proxy à jour
- [`phase-2-acceptance.md`](phase-2-acceptance.md) — matrice de preuves Phase 2
- [`integration-audit-remediation.md`](integration-audit-remediation.md) — audit du 2026-08-03
- [`window-taskbar-export-plan.md`](window-taskbar-export-plan.md) — plan d'extraction fenêtres/taskbar
- [`mobile-interface-platform-plan.md`](mobile-interface-platform-plan.md) — proposition plateforme Mobile
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md) — proposition (non engagée) de compilation/exécution hybride WASM/serveur de code C# utilisateur
