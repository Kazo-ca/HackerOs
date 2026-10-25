# Analyse des manques pour un véritable écosystème WebAssembly

**Statut :** analyse exploratoire — non implémentée, aucune décision `D-xxx`
actée, aucun code écrit.

## 1. Objet

`integration-task-list.md` documente exhaustivement le travail restant pour
les phases déjà engagées (1 à 6). Ce document couvre autre chose : des
manques qui ne figurent **nulle part** dans ce plan — ni comme tâche ouverte,
ni comme exclusion délibérée — repérés en se demandant ce qu'un développeur
ou un utilisateur attendrait d'un « vrai » écosystème WebAssembly, au-delà de
la seule faisabilité technique de charger des apps.

Deux points ont été jugés assez structurants pour recevoir une entrée dans le
Registre des problèmes (`P-014`) et une place réservée dans les phases
existantes ; cinq autres ont reçu une entrée dans le Registre des suggestions
(`S-011` à `S-016`, section 40). Le reste reste à l'état de discussion —
volontairement, faute de matière suffisante pour un item de plan actionnable.

## 2. Deux manques structurants

### 2.1 Isolation d'exécution entre apps (pas seulement contre le code malveillant)

**Registre des problèmes : `P-014`.**

`P-009` couvre déjà l'absence d'isolation contre du *code malveillant* :
« Runtime assemblies share one .NET process and are not malicious-code
isolated. » C'est un problème de confiance/sécurité, résolu en aval par
`D-020`.

Le manque relevé ici est différent : même une app **honnête mais buguée**
(boucle infinie, calcul synchrone lourd) bloque aujourd'hui **tout l'OS**, pas
seulement elle-même. Toutes les apps tournent sur le même thread WASM
principal du navigateur — il n'existe ni Web Worker par app, ni watchdog, ni
timeout, ni mécanisme de récupération. C'est un problème de *résilience*, pas
de *malveillance* : même un futur modèle de confiance/signature (`D-019`)
n'y changerait rien, puisqu'il ne protège que contre l'intention, pas contre
le bug.

Le nouveau `docs/user-code-compilation-execution-plan.md` effleure déjà le
sujet, mais seulement pour le cas particulier du code utilisateur compilé à
la volée (« Worker dédié sans binding JS-interop... communication limitée à
un canal `postMessage` typé »). Rien n'existe pour les apps normales,
premières ou tierces parties, qui tournent toutes aujourd'hui in-process.

### 2.2 Distribution et découverte de packages

**Registre des suggestions : `S-011`.**

La Phase 6 (`integration-task-list.md` section 33) répond entièrement à la
question « peut-on installer un package au runtime ? » — jamais à « où le
trouve-t-on ? ». Aucun registre, catalogue, recherche, ni même le concept
d'un « store » pour des apps tierces n'existe dans le plan. Sans ça, une
Phase 6 techniquement réussie donnerait un mécanisme d'installation sans
rien à installer — pas un écosystème.

## 3. Cinq manques additionnels (Registre des suggestions)

| Manque | ID | Emplacement dédié |
|---|---|---|
| Cache/partage de dépendances entre apps chargées indépendamment | `S-012` | Phase 3 — Build-Known Lazy Loading |
| Permissions à la demande (au lieu de tout déclarer au manifeste et accorder au login) | `S-013` | Extension de la Phase 1 — Policy/Capability Grants |
| Deep-linking externe (gestionnaire de protocole `web+hackeros://`) | `S-014` | Phase 2 — PWA Packaging |
| Mise à jour indépendante d'une app installée (vs mise à jour PWA globale atomique) | `S-015` | Phase 6 — Package Format |
| Boucle de développement locale (lancer/hot-reload une app dans HackerOS sans rebuild complet) | `S-016` | Phase 3 — Public SDK |

Détail de chacun :

- **Cache/partage de dépendances (`S-012`)** — chaque app *build-known lazy*
  (`docs/lazy-loading.md`) semble apporter ses propres assemblys
  indépendamment ; rien ne dit si deux apps référençant la même bibliothèque
  partagée dédupliquent au téléchargement ou en mémoire. À mesure que plus
  d'apps sont lazy-loaded, ce coût redondant grandit.
- **Permissions à la demande (`S-013`)** — le modèle actuel
  (`CleanProfileCapabilityGrantSeeder`) accorde toutes les capacités
  déclarées au manifeste dès le login. Aucune demande de capacité *au moment
  du besoin* (type « autoriser l'accès à la webcam maintenant »), contrairement
  à la plupart des écosystèmes modernes (navigateur, mobile).
- **Deep-linking externe (`S-014`)** — pas de gestionnaire de protocole
  enregistré pour ouvrir une app/intent HackerOS depuis un lien situé en
  dehors de la PWA elle-même.
- **Mise à jour indépendante d'une app (`S-015`)** — le modèle de mise à jour
  actuel (`P2-PWA-*`) est celui de la PWA entière, atomique, tout ou rien.
  Une fois la Phase 6 en place, rien ne prévoit de versionner/mettre à jour
  une app tierce indépendamment du reste.
- **Boucle de développement locale (`S-016`)** — le SDK a un validateur de
  manifeste (`Tools/HackerOs.Tools.ManifestValidator`) et des templates
  `dotnet new`, mais rien pour lancer/tester son app *dans* HackerOS
  localement avec hot-reload, sans passer par le build complet de
  `HackerOs.Ecosystem`.

## 4. Manques relevés mais non promus (discussion seulement)

Ces points restent à l'état de discussion : trop tôt pour un item de plan
actionnable, faute de décision produit ou de matière technique suffisante
pour écrire une portée concrète. Ils sont notés ici pour ne pas les perdre.

- **Interop WASM non-.NET** — l'écosystème entier est verrouillé sur
  Blazor/Mono. Aucun mécanisme pour charger un module WASM écrit dans un
  autre langage (Rust, C, AssemblyScript — pas de WASI, pas de component
  model). Si « vrai écosystème WebAssembly » implique interopérer avec le
  monde WASM au sens large plutôt que juste « du .NET compilé en wasm »,
  c'est un manque conceptuel plus qu'une tâche à planifier.
- **Communication inter-app au-delà des intents typés** — les intents
  couvrent lancement/ouverture de fichier/exécution de commande, mais pas de
  bus pub/sub, pas de « share sheet » générique, pas d'échange de données en
  temps réel entre deux apps ouvertes simultanément.
- **Télémétrie/rapport de crash pour apps tierces** — le diagnostic/logging
  existant sert la plateforme elle-même ; rien n'est exposé comme SDK pour
  qu'une app tierce y adhère volontairement.
- **Signaux de confiance communautaires** — `D-019` couvre la
  signature/trust store *techniques*, mais rien sur la couche sociale (notes,
  avis, vérification d'éditeur, réputation) qu'un écosystème utilisateur a
  généralement.
- **Recherche globale (« spotlight »)** — pas de recherche unifiée à travers
  apps, fichiers, et commandes.
- **Widgets / mini-surfaces de bureau** — rien au-delà des fenêtres complètes
  gérées par `HackerOs.Windowing.Core`.
- **Monétisation/licence** — absent partout ; pertinent seulement si
  l'ambition inclut des développeurs tiers rémunérés.

## 5. Documents de référence

- [`integration-task-list.md`](integration-task-list.md) — sections 33 (Phase
  6), 39 (Registre des problèmes, `P-014`), 40 (Registre des suggestions,
  `S-011` à `S-016`)
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md) —
  isolation par Web Worker pour le code utilisateur compilé, angle voisin de
  la section 2.1
- [`lazy-loading.md`](lazy-loading.md) — mécanisme actuel de chargement
  différé, base du manque `S-012`
- [`server-implementation-pass.md`](server-implementation-pass.md) —
  patron proxy/fallback réutilisable pour une éventuelle voie serveur de
  distribution de packages
- [`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md) —
  séquencement global dans lequel situer ces manques une fois priorisés
