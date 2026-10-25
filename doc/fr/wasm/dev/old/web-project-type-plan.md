# Plan technique — Type de projet "Web" (ASP.NET/Blazor hébergé, WebAssembly ou Server)

**Statut :** proposition technique — non implémentée, aucune décision `D-xxx`
actée, aucun code écrit. Les choix ci-dessous reflètent les préférences déjà
exprimées par le user en session (2026-08-17) sur les quatre questions
structurantes ; ils doivent encore devenir des ADR formelles avant tout code
(voir §9).

## 1. Objectif

Ajouter un quatrième `AppKind` — **`Web`** — à côté de `Window`, `Terminal` et
`Service` : un projet ASP.NET Core (ou Razor Class Library Blazor) référencé
par le host peut être exposé dans l'écosystème HackerOS et devenir accessible
depuis l'app **Browser** (`org.hackeros.browser`) via un nom de domaine, avec
deux modes d'exécution :

- **WebAssembly** — le projet est une assembly Blazor WASM chargée en lazy-load
  à la demande, comme les apps système existantes (`docs/lazy-loading.md`).
- **Server** — le projet s'exécute côté `Server/HackerOs.Server` et est atteint
  depuis le Browser via le pont proxy déjà en place (ADR 0028).

Si aucun domaine n'est déclaré, le domaine par défaut dérive du nom du projet
(assembly/namespace). Un bloc de manifeste optionnel (`Web`, dans
`app.manifest.json`, sur le même modèle que le bloc `Terminal` existant) peut
préciser le domaine, le mode d'exécution et des options avancées.

L'éditeur de code doit, à terme, permettre de créer ce type de projet
directement depuis HackerOS — voir §8 pour la portée retenue de cette
itération.

## 2. Contexte et documents liés

- [ADR 0021](../adr/0021-simulated-network-and-browser-rendering.md) — a
  délibérément rejeté le rendu HTML brut/iframe pour les **sites simulés**
  (`ISimulatedWebsiteController`/`SimulatedPage`), pour des raisons de
  testabilité et de sécurité XSS. Ce plan **ne remet pas en cause cette
  décision** : les projets Web réels sont un mécanisme séparé et parallèle
  (§4, §6), pas une extension du modèle `SimulatedPage`.
- [ADR 0023](../adr/0023-optional-game-domain-and-proxy-fallback.md) — établit le
  pattern de gateway à double implémentation (locale / proxy serveur /
  fallback) via DI + capacité manifeste, repris ici pour la résolution
  WebAssembly-vs-Server et pour la résolution DNS unifiée (§7).
- [ADR 0027](../adr/0027-server-hosted-blazor-ui.md) et
  [ADR 0028](../adr/0028-client-side-server-connection.md) — `Server/HackerOs.Server`
  héberge déjà l'arbre de composants Razor et un `IProxyClient` pour les
  commandes réseau réelles ; le mode Server de ce plan réutilise ces deux
  mécanismes plutôt que d'en inventer un troisième.
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md) —
  plan sœur (même statut : proposition non actée) pour la compilation Roslyn
  et l'exécution de C# utilisateur, local (WASM) ou serveur. La Phase 2 de
  l'éditeur de code (§8) en dépend directement et ne peut pas avancer avant
  qu'il soit accepté.
- [`docs/lazy-loading.md`](lazy-loading.md) — mécanisme de lazy-load
  (`BlazorWebAssemblyLazyLoad`, `WebAssemblyLazyAssemblyTransport`,
  `BuildKnownLazyAppDescriptorRegistry`) réutilisé tel quel pour le mode
  WebAssembly (§5).
- [ADR 0020](../adr/0020-editor-framework-and-script-sandbox.md) et
  [`code-editor.md`](code-editor.md) — bornent strictement l'éditeur à
  `vfs.read`/`vfs.write` ; toute exécution reste derrière
  `user-scripts.execute`. Le scaffolding de fichiers (§8, phase 1) respecte
  cette limite ; l'aperçu compilé (§8, phase 2) ne le peut pas sans la
  décision `D-xxx` du plan de compilation/exécution.
- `Shared/HackerOs.App.Abstractions/AppManifest.cs`, `AppKind.cs` — modèle de
  manifeste existant, point d'ancrage du nouveau bloc `Web` (§4).
- `Shared/HackerOs.Simulation.Abstractions/Network/SimulatedNetworkServiceContracts.cs` —
  `ISimulatedWebsiteRegistry`/`ISimulatedDns` existants, non modifiés par ce
  plan (§7 ajoute un registre parallèle, pas une extension de ceux-ci).

## 3. Deux voies de production, un seul modèle d'exécution runtime

Deux mécanismes distincts alimentent le même concept runtime (une app
`AppKind.Web` adressable par domaine) :

1. **Référence de projet build-time** — un développeur ajoute un vrai
   `.csproj` ASP.NET/Blazor comme référence de projet au host (même famille
   que la découverte d'assembly référencée explicite décrite par `P1-APP-002`
   pour les apps `Window`/`Terminal`/`Service` existantes). C'est le scénario
   demandé en premier lieu : "un projet asp.net ajouté comme référence au
   projet host".
2. **Auteur runtime via l'éditeur de code** — un utilisateur du jeu crée un
   projet Web depuis l'app Code Editor, sans toucher au host côté
   développeur. Ce chemin dépend du plan de compilation/exécution hybride
   (§8, phase 2) et est hors scope pour la première itération.

Les deux voies convergent vers le même manifeste (`AppKind.Web`, bloc `Web`),
le même mécanisme de résolution de domaine (§7) et le même composant de rendu
dans le Browser (§6). La différence porte uniquement sur *comment* l'assembly
ou le code source arrive dans le système, pas sur *comment* il s'exécute une
fois là. Cette convergence évite de dupliquer la logique de rendu/résolution
entre le cas "projet du dépôt" et le cas "projet créé en jeu", exactement
comme le plan de compilation/exécution évite de dupliquer la logique de
compilation entre client et serveur.

## 4. `AppKind.Web` et modèle de manifeste

### 4.1 Nouvelle valeur d'énumération

```csharp
// Shared/HackerOs.App.Abstractions/AppKind.cs
public enum AppKind
{
    Window,
    Terminal,
    Service,
    Web, // nouveau
}
```

### 4.2 Nouveau bloc de manifeste optionnel

Sur le modèle exact de `TerminalCommandManifest Terminal` déjà présent sur
`AppManifest` :

```csharp
/// <summary>Gets hosting metadata for a Web application.</summary>
public WebAppManifest? Web { get; init; }

/// <param name="Domain">
/// Domaine explicite (ex. "mycompany.hos"). Si null, le domaine par défaut
/// dérive du nom d'assembly/namespace du projet — voir §4.3.
/// </param>
/// <param name="ExecutionMode">WebAssembly (lazy-load client) ou Server (hébergé par HackerOs.Server).</param>
/// <param name="RootComponentType">
/// Nom qualifié du composant Razor racine à monter (mode WebAssembly
/// uniquement). Convention par défaut si absent : voir §5.2.
/// </param>
public sealed record WebAppManifest(
    string? Domain,
    WebExecutionMode ExecutionMode,
    string? RootComponentType = null);

public enum WebExecutionMode
{
    WebAssembly,
    Server,
}
```

`AppManifestValidator` gagne une règle symétrique à celle qui exige déjà le
bloc `Terminal` pour `AppKind.Terminal` : le bloc `Web` est optionnel (tout
comme le champ `Domain` en son sein), mais si présent, il n'est valide que
pour `AppKind.Web` (même pattern que le rejet actuel de `dialogs.*` pour les
apps non-`Window`, `P1-CAP-003`).

**Le manifeste complet reste requis** — HackerOS n'a pas aujourd'hui de
notion d'app sans `app.manifest.json` (schéma, Id, capacités, etc. sont tous
`required`). "Manifeste optionnel" pour ce plan signifie : *le bloc `Web` et
son champ `Domain`* sont optionnels, pas le manifeste dans son ensemble.

### 4.3 Résolution du domaine par défaut

Ordre de résolution, du plus spécifique au plus générique :

1. `manifest.Web.Domain` si renseigné.
2. Sinon, nom d'assembly normalisé du point d'entrée
   (`manifest.EntryPoint.Assembly`, ex. `MyCompany.Website` →
   `mycompany-website`), en minuscules, avec les points remplacés par des
   tirets (évite toute collision avec la syntaxe de sous-domaine).
3. Un suffixe de zone déterministe (ex. `.hos` ou `.local`, à trancher en ADR)
   distingue ce zonage des domaines simulés existants (`hackersearch.net`,
   etc.) pour qu'un lecteur/joueur puisse visuellement distinguer un site
   simulé narratif d'une app Web réelle du build.

## 5. Mode WebAssembly — lazy-load + montage de composant

### 5.1 Chargement

Réutilisation intégrale du mécanisme existant (`docs/lazy-loading.md`) :

- L'assembly du projet Web est déclarée `<BlazorWebAssemblyLazyLoad>` dans
  `HackerOs.Ecosystem.csproj`, exactement comme les apps système actuelles.
- Son `app.manifest.json` est embarqué et découvert par
  `BuildKnownLazyAppDescriptorRegistry` au même titre que les manifestes
  `Window`/`Terminal`/`Service`.
- Le chargement effectif se déclenche à la première navigation du Browser
  vers son domaine (pas au lancement d'une "app" depuis le launcher — une app
  `Web` n'apparaît pas dans le launcher/taskbar par défaut, elle est
  adressée par domaine ; `Presentation` reste néanmoins requis par le modèle
  actuel, à minima pour l'inventaire/les paramètres).

### 5.2 Montage — pas d'iframe

Le composant racine déclaré (`WebAppManifest.RootComponentType`, ou par
convention le seul type public implémentant un nouveau marqueur
`IWebAppRootComponent` dans l'assembly si non déclaré explicitement) est
instancié dynamiquement et monté **directement comme fragment de rendu** dans
le panneau de contenu du Browser — aucun iframe, aucune sérialisation
HTML. C'est le même modèle de confiance que n'importe quelle autre app
Blazor de premier niveau du système : le projet est du C#/Razor compilé avec
le host, pas du contenu utilisateur non fiable.

Cette voie ne convient qu'à des projets Blazor (Razor Class Library ciblant
WASM) — pas à de l'ASP.NET Core MVC/Razor Pages classique, qui suppose un
vrai cycle requête/réponse HTTP. Un projet MVC/Razor Pages classique doit
utiliser le mode Server (§6).

## 6. Mode Server — hébergement dans `HackerOs.Server`, accès via proxy

### 6.1 Hébergement

Le projet ASP.NET référencé est déployé/exécuté **à l'intérieur du process
`Server/HackerOs.Server`** existant, aux côtés de ses API sync/identité/proxy
actuelles (ADR 0027 y a déjà ajouté un troisième mode d'hébergement Razor —
ce plan en ajoute un quatrième, dédié aux projets Web référencés).

Deux approches possibles pour le montage concret, à trancher en ADR
dédiée :

- **Sous-app mappée par domaine/chemin** — chaque projet Web référencé est
  enregistré comme groupe d'endpoints minimal API / middleware de branche
  (`app.MapWhen`/`UseWhen` sur l'en-tête `Host` ou un préfixe de chemin
  dérivé du domaine résolu en §4.3), dans le même process, à la compilation.
- **Isolation renforcée** — reprend la réflexion déjà actée dans
  `user-code-compilation-execution-plan.md` §3 sur l'absence d'isolation
  réelle en WASM et la nécessité d'une isolation OS côté serveur (conteneur
  ou process séparé). Pour ce plan-ci, la distinction de confiance est
  importante : un projet référencé au build-time par le développeur du host
  n'a **pas** le même modèle de menace qu'un futur projet compilé à la volée
  depuis du code utilisateur (Phase 2, §8) — le premier peut raisonnablement
  partager le process `HackerOs.Server` sans sandbox supplémentaire ; le
  second, non. Ce plan ne pré-approuve donc l'hébergement in-process que
  pour les projets référencés au build-time.

### 6.2 Accès depuis le Browser

Le Browser n'ouvre jamais de connexion HTTP directe vers l'hôte réel. Il
passe par `IProxyClient` (ADR 0028), exactement comme le fallback réel de
`ping`. La requête proxyée cible `HackerOs.Server` avec le domaine résolu
(§4.3) transmis en en-tête ou en chemin, pour que le middleware §6.1 route
vers la bonne sous-app.

Le corps de réponse réel (HTML/CSS/JS) transite par le pont proxy — ce plan
hérite donc de la limitation déjà notée par l'ADR 0028 : `ProxyHttpResponse`
est aujourd'hui **metadata-only** (pas de streaming de corps binaire/texte).
Le mode Server de ce plan est donc bloqué tant que cette lacune
(`docs/server-implementation-pass.md`, "Pass N+1a") n'est pas comblée — à
noter explicitement comme prérequis, pas comme detail d'implémentation.

### 6.3 Rendu — iframe sandboxée

Contrairement au mode WebAssembly (§5.2), le contenu reçu ici est du HTML
réel produit par un vrai serveur HTTP, pas un arbre de composants C#. Il est
affiché via une **iframe sandboxée** (`sandbox="allow-scripts"` a minima,
`srcdoc` alimenté par le corps proxyé une fois le blocage §6.2 levé, CSP
restrictive). Ce point réintroduit délibérément ce que l'ADR 0021 a écarté
pour les sites *simulés* — d'où la nécessité d'une **nouvelle ADR** qui
documente explicitement cette exception comme scopée aux apps Web
*hébergées réelles*, jamais aux sites simulés existants
(`ISimulatedWebsiteController` reste inchangé, structuré, sans HTML brut).

## 7. Résolution de domaine dans le Browser

### 7.1 Registre séparé

Nouveau contrat, parallèle à `ISimulatedWebsiteRegistry` plutôt qu'une
extension de celui-ci (qui reste scopé aux sites simulés structurés) :

```csharp
public interface IHostedWebAppRegistry
{
    HostedWebAppDescriptor? FindByDomain(string domain);
}

public sealed record HostedWebAppDescriptor(
    string Domain,
    string AppId,
    WebExecutionMode ExecutionMode);
```

Alimenté au boot à partir des manifestes `AppKind.Web` découverts (même
pipeline que `AppLifecycleOrchestrator`/`AppEntryPointDiscovery` pour les
autres `AppKind`), avec le domaine déjà résolu selon §4.3.

### 7.2 Ordre de résolution unifié

Pour qu'une seule adresse tapée dans la barre du Browser (et, à terme,
`curl`) se comporte de façon cohérente, la résolution suit un ordre
déterministe, sur le même principe de fallback déjà établi par l'ADR 0023
(`IGameDomainGateway.IsAvailable`) et l'ADR 0028 (proxy réseau réel) :

1. `ISimulatedDns`/`ISimulatedWebsiteRegistry` — sites simulés narratifs
   existants (inchangé).
2. `IHostedWebAppRegistry` — apps Web réelles de ce plan.
3. Fallback réseau réel via `IProxyClient`, si connecté (ADR 0028), pour tout
   domaine non résolu par 1 ou 2.

Cet ordre est délibérément centralisé dans un seul point de résolution
partagé par le Browser et par les commandes réseau (`curl`, `ping`), plutôt
que chacun interrogeant les trois sources indépendamment — évite une
divergence de comportement entre "naviguer vers X" et "curl X".

## 8. Éditeur de code — création de projets Web

Décision du user : scaffolding pur pour cette itération ; l'aperçu
compilé/exécuté est planifié mais explicitement **après** l'acceptation du
plan de compilation/exécution hybride. Deux phases donc, la seconde n'étant
pas engagée par ce document :

### Phase 1 — Scaffolding (dans la portée de ce plan une fois accepté)

- Nouvelle action dans Code Editor ("Nouveau projet Web") qui écrit dans le
  VFS app-scoped un squelette de fichiers : `Program.cs` (ou `App.razor` +
  `Pages/`), `wwwroot/` minimal, et un `app.manifest.json` pré-rempli avec
  `Kind: "web"`, un bloc `Web` dont `Domain` est vide (pour forcer la
  résolution par défaut §4.3 ou inviter l'utilisateur à le renseigner), et
  `ExecutionMode: "webAssembly"` par défaut.
- Reste strictement dans les capacités `vfs.read`/`vfs.write` déjà couvertes
  par ADR 0020 — aucune capacité nouvelle, aucune compilation, aucune
  exécution. Les fichiers créés ne deviennent une app `Web` fonctionnelle que
  s'ils sont ensuite intégrés comme référence de projet build-time (§3,
  voie 1) — le scaffolding runtime ne rend pas, à lui seul, le site
  navigable depuis le Browser.

### Phase 2 — Aperçu compilé/exécuté (hors scope, dépend de D-xxx externe)

- Une fois `user-code-compilation-execution-plan.md` accepté et sa Phase
  serveur/local implémentée, la Phase 2 de ce plan branche l'aperçu Code
  Editor sur `ICompilationService`/`IExecutionSandbox` (voie 2 de §3), puis
  réutilise **le même composant de montage** que le mode WebAssembly (§5.2)
  pour l'aperçu live — pas un troisième mécanisme de rendu.
- Ne pas commencer cette phase avant la décision `D-xxx` du plan sœur.

## 9. Décisions D-xxx restant à formaliser (prochaines étapes)

Ce document fige les préférences exprimées en session, mais rien n'est
`Accepted` tant que des ADR dédiées n'existent pas. Découpage recommandé,
dans cet ordre (chaque étape peut être une ADR séparée) :

1. **ADR — `AppKind.Web`, manifeste, mode WebAssembly.** Couvre §4 et §5 :
   nouvelle valeur d'enum, `WebAppManifest`, résolution de domaine par
   défaut, montage de composant sans iframe. Ne dépend de rien d'autre que
   l'existant.
2. **ADR — Hébergement Server dans `HackerOs.Server` et exception iframe.**
   Couvre §6 : montage sous-app, routage proxy par domaine, et l'exception
   explicite à l'ADR 0021 pour le rendu iframe scopé aux apps hébergées
   réelles. Dépend de (1) pour le modèle de manifeste, et bloquée par le
   streaming de corps binaire du proxy (`server-implementation-pass.md`,
   Pass N+1a).
3. **ADR — `IHostedWebAppRegistry` et résolution de domaine unifiée.** Couvre
   §7. Dépend de (1) et (2).
4. **Extension au plan `user-code-compilation-execution-plan.md`** pour
   brancher la Phase 2 de l'éditeur (§8) une fois ce plan sœur lui-même
   accepté — pas une ADR de ce plan-ci.

## 10. Hors scope pour l'instant

- Hébergement de projets Web sur un serveur réel externe non opéré par
  `HackerOs.Server` (écarté par la décision du user en §Contexte).
- Multi-tenance/isolation multi-utilisateur des apps Server hébergées —
  hérite de la limitation single-tenant déjà actée par l'ADR 0027.
- Compilation/exécution à la volée de projets Web créés en jeu (Phase 2,
  §8) tant que le plan sœur n'est pas accepté.
- Extension d'`ISimulatedWebsiteController`/`ISimulatedPage` — le modèle de
  sites simulés reste inchangé et séparé (§7.1).
- Streaming de corps binaire pour `IProxyClient` — prérequis externe déjà
  identifié par l'ADR 0028, pas repris ici en détail.
- Suffixe de zone DNS définitif pour les domaines Web réels (`.hos` vs
  autre) — à trancher dans l'ADR (1).

## 11. Documents de référence

- [ADR 0020](../adr/0020-editor-framework-and-script-sandbox.md)
- [ADR 0021](../adr/0021-simulated-network-and-browser-rendering.md)
- [ADR 0023](../adr/0023-optional-game-domain-and-proxy-fallback.md)
- [ADR 0027](../adr/0027-server-hosted-blazor-ui.md)
- [ADR 0028](../adr/0028-client-side-server-connection.md)
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md)
- [`lazy-loading.md`](lazy-loading.md)
- [`hosting-model.md`](hosting-model.md)
- [`code-editor.md`](code-editor.md)
- [`server-implementation-pass.md`](server-implementation-pass.md)
