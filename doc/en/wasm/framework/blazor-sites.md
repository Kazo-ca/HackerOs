# Sites Blazor : composants dessinés par le navigateur

Chantier `web/9-blazor-sites` de la phase web (ASP.Hack). Décision :
[ADR 0084](../dev/adr/0084-blazor-sites.md) (complète l'ADR 0073 §4, l'ADR 0075 §5 et
l'ADR 0081 §2). Plan : [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md) §5. S'appuie sur
`web/4` ([`browser-html-rendering.md`](../dev/browser-html-rendering.md)) pour le
navigateur, `web/6` ([`asp-hack-mvc.md`](asp-hack-mvc.md)) pour le site et
`web/10` ([`detached-display-windows.md`](../dev/detached-display-windows.md)) pour la
fenêtre détachée.

## Objet

Un site interne peut être **interactif** : ses pages sont des composants Blazor
dessinés par le navigateur de HackerOS directement dans la zone de page, comme
un site Blazor WebAssembly dans un vrai navigateur.

- Le site reste une app `Web` ordinaire (ADR 0073) : il écoute, sert ses routes
  MVC ou minimales, et déclare en plus un **composant racine**
  (`AppDefinition.BlazorRoot`).
- Le navigateur reconnaît la **page hôte** du site (elle charge
  `_framework/blazor.hack.js`), lit `blazor.boot.json` et demande à l'OS de
  démarrer un **processus de rendu** (`--type=renderer`, comme Chrome) sous
  l'identité du site et de l'utilisateur de l'onglet.
- Le site **navigue par l'URL de l'onglet** : ses liens changent l'adresse sans
  requête, précédent et suivant restent dans le site, la barre d'adresse et
  Recharger font une vraie requête.
- Le site n'a **aucune capacité `filesystem.*`** (il n'en déclare pas) ; il
  appelle ses propres routes par `Site.Http`, un client HTTP sur son adresse
  avec les cookies de l'onglet.
- Une exception du site donne la page « Aw, Snap! » du navigateur ; l'OS n'est
  jamais touché.

Le site d'exemple **Blazor Sample Site** (`http://blazor-sample.hack/`) est
installé dans les deux hôtes : compteur sans rechargement, page avec paramètre
de route qui appelle l'API du site, page qui plante.

## Architecture

```text
 Navigateur (fenêtre, processus du navigateur)
 ├─ GET http://site.hack/page ───────────────► site (processus web, HackWebHost)
 │   ◄── page hôte (<script src="_framework/blazor.hack.js">)      │ routes MVC/minimales d'abord,
 ├─ GET /_framework/blazor.boot.json ──────────────────────────────┘ puis page hôte, chargeur, boot.json
 ├─ IHackWebSiteHost.StartAsync(page, boot, contexte du navigateur)
 │     └─ HackWebSiteHost (Platform.Blazor) : app Web qui sert l'origine, racine chargée,
 │        AppLifecycleOrchestrator.StartAuxiliaryAsync → processus de rendu (enfant, auxiliaire)
 └─ <HackWebSiteView Process Url Cookies …>            zone de page, sans iframe
       ├─ CascadingValue IAppExecutionContext (processus de rendu)
       ├─ CascadingValue HackWebSite (Navigation, Http, Title)
       ├─ HackSiteErrorBoundary ─► OnFaulted → « Aw, Snap! », processus en défaut
       └─ composant racine du site (HackSiteRouter → pages @page, layouts, HackNavLink, HackPageTitle)
             clics sur <a href> capturés par HackWebSiteView.razor.js → navigation interne ou du navigateur
```

| Fichier | Rôle |
|---|---|
| `Shared/HackerOs.App.Abstractions/Definition/AppDefinition.cs` | `BlazorRoot` (`Type?`) ; le générateur écrit `web.blazor { assembly, type }` (schéma, sérialiseur, validateur `manifest.web.blazor.assembly`). |
| `Shared/HackerOs.AppSdk/Net/Hosting/HackBlazorBoot.cs` | `HackBlazorBootManifest` (`appId`, `appName`, `entryAssembly`, `rootComponent`), chemins `_framework/blazor.hack.js` et `_framework/blazor.boot.json`. |
| `Shared/HackerOs.AppSdk/Net/HackHttp.cs` | `HackHttpClient.BaseAddress` : URI relatives résolues comme `HttpClient.BaseAddress`. |
| `Shared/HackerOs.AppSdk.Web/Hosting/HackBlazorSiteEndpoints.cs` | Les trois réponses d'un site à racine Blazor, en dernier (`MapFallback`, ordre `1 000 000`) ; `IsHostPage(html)`. |
| `Shared/HackerOs.AppSdk.Web/Hosting/HackWebApplication.cs`, `HackWebHost.cs` | `MapFallback(pattern, handler)` ; `CreateApplication` ajoute les endpoints Blazor après `Configure`. |
| `Shared/HackerOs.AppSdk.Web.Blazor/Sites/` | SDK Blazor des sites : `IHackWebSiteHost`, `IHackWebSiteProcess`, `HackWebSiteView`, `HackWebSite`, `HackSiteNavigationManager`, `HackSiteRouter`, `HackSiteLayoutBase`, `HackNavLink`, `HackPageTitle`, `HackSiteErrorBoundary`. |
| `Platform/HackerOs.Platform.Blazor/Sites/HackWebSiteHost.cs` | Implémentation : trouve l'app par `web.urls`, refuse un `boot.json` d'une autre app, charge l'assembly (paresseux), vérifie la racine, démarre le processus de rendu ; `RendererProcess` relaie `Exited`. |
| `Platform/HackerOs.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs` | `StartAuxiliaryAsync(appId, principal, parentPid, argv)` : processus `Auxiliary`, capacités du site moins `network.listen*`, suivi comme une fenêtre (`StopAsync`). |
| `Platform/HackerOs.Platform.Core/Processes/InMemoryProcessManager.cs` | `ProcessStartRequest.Auxiliary` ; `TryGetSingleton` ignore les processus auxiliaires. |
| `Apps/System/HackerOs.Apps.Browser/BrowserWindow.razor(.cs)` | Détection de la page hôte, `TryStartSiteAsync`, branche `ShowsSite`, navigation interne (précédent/suivant), « Aw, Snap! », arrêt du processus à la sortie de la page ; zone de page rendue à une seule place à travers `DisplaySurfaceContent`. |
| `Apps/System/HackerOs.Apps.Browser/BrowserErrorPages.cs` | `ForRendererCrash` (`STATUS_ACCESS_VIOLATION`, `RESULT_CODE_KILLED`), `ForSiteNotStarted` (`ERR_SITE_NOT_STARTED`). |
| `Shared/HackerOs.AppSdk.Blazor/Display/DisplaySurfaceContent.razor.cs` | `Surface` devient facultatif : sans surface, le contenu reste sur place ; un contenu toujours rendu par le portail garde ses composants au détachement. |
| `Apps/Samples/HackerOs.Samples.BlazorSite/` | Site d'exemple : `BlazorSampleSite` (définition, `/api/greeting/{name}`), `App.razor`, `Layout/MainLayout`, `Pages/Index`, `Pages/Hello`, `Pages/Crash`. Référencé par `OS/HackerOs.Ecosystem` et `test/test`. |

### Cycle de vie du processus de rendu

1. Le navigateur reçoit un `text/html` qui contient `_framework/blazor.hack.js`
   et lit `blazor.boot.json` (visible dans l'onglet Network).
2. `HackWebSiteHost` retrouve l'app `Web` installée dont une URL de `web.urls`
   a le schéma, l'hôte et le port de la page ; refuse si `boot.json` nomme une
   autre app, si l'app n'a pas de `web.blazor`, ou si le type n'est pas un
   composant.
3. `StartAuxiliaryAsync` démarre le processus : enfant du processus du
   navigateur, `argv` `[<app>, --type=renderer, <url>]`, utilisateur de
   l'onglet, capacités accordées au site moins `network.listen` et
   `network.listen.privileged`. Il apparaît dans le gestionnaire de tâches sous
   l'identifiant de l'app du site ; `TryGetSingleton` l'ignore, donc l'activation
   à la demande du site (ADR 0074 §3) n'est pas affectée.
4. Le processus se termine quand l'onglet quitte le document, se ferme, recharge
   (`IHackWebSiteProcess.StopAsync`) ; une exception non gérée l'arrête en
   `Fault` ; `kill` ou le gestionnaire de tâches annulent son jeton, la vue
   reçoit `Exited` et le navigateur montre « Aw, Snap! » (`RESULT_CODE_KILLED`).

### Navigation

- `HackWebSiteView.razor.js` écoute `click` en phase de capture sur la zone de
  page : un `<a href>` du site est arrêté avant le routeur Blazor de l'OS (qui
  écoute sur `document`) et remis à `OnLinkClicked`. Clics modifiés, bouton
  secondaire, `download`, `javascript:` passent.
- Dans le site et sans `forceLoad` : `HackSiteNavigationManager.SetLocation`
  (le `LocationChanged` du site), puis `OnLocationChanged` → le navigateur met à
  jour l'adresse et l'historique (`pushState`/`replaceState`), aucune requête.
- Ailleurs, ou `forceLoad` : `OnNavigate` → navigation ordinaire du navigateur,
  qui arrête le processus de rendu.
- Un lien dont `target` existe et n'est pas `_self` (`_top`, `_blank`, un nom)
  est une navigation du document même vers le site, comme dans Blazor
  (ADR 0087 §1) : c'est ainsi qu'une page Blazor rejoint les pages MVC du même
  site (`<a href="/Notes" target="_top">` dans l'exemple Intranet).
- Précédent/suivant : si la cible est dans le site vivant, le navigateur change
  l'adresse de la vue (`Url`), qui appelle `SetLocation` ; sinon vraie requête.
  Recharger fait toujours une vraie requête (nouveau processus de rendu).

## Usage

### Déclarer un site Blazor

```csharp
[AppManifest]
public sealed partial class Intranet : WebAppDefinition
{
    public override string Id => "com.example.intranet";
    public override string Name => "Intranet";
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
    public override Type? BlazorRoot => typeof(App);
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite];

    public override void Configure(HackWebApplicationBuilder builder)
    {
        HackWebApplication app = builder.Build();
        app.MapGet("/api/notes", () => HackResults.Json(...));   // routes de données
        app.MapControllers();                                     // MVC éventuel
        // la page hôte, blazor.hack.js et blazor.boot.json sont ajoutés après Configure
    }
}
```

Le projet référence `HackerOs.AppSdk.Web` et `HackerOs.AppSdk.Web.Blazor` ;
`_Imports.razor` fait hériter les composants de `HackComponentBase`
(`@inherits HackComponentBase`) et importe `HackerOs.AppSdk.Web.Blazor.Sites`.

### Composant racine, pages, layout

```razor
@* App.razor *@
<HackSiteRouter AppAssembly="typeof(App).Assembly" DefaultLayout="typeof(MainLayout)">
    <NotFound><p>Rien ici.</p></NotFound>
</HackSiteRouter>

@* Layout/MainLayout.razor *@
@inherits HackSiteLayoutBase
<nav><HackNavLink href="/" Match="NavLinkMatch.All">Accueil</HackNavLink> <HackNavLink href="/notes">Notes</HackNavLink></nav>
<main>@Body</main>

@* Pages/Note.razor *@
@page "/notes/{Id:int}"
<HackPageTitle Title="@($"Note {Id}")" />
@code {
    [Parameter] public int Id { get; set; }
    [CascadingParameter] HackWebSite Site { get; set; } = default!;
    protected override async Task OnInitializedAsync() =>
        _note = await Site.Http.GetFromJsonAsync($"api/notes/{Id}", NotesJson.Default.Note);
}
```

| Type (`HackerOs.AppSdk.Web.Blazor.Sites`) | Rôle |
|---|---|
| `HackWebSite` (cascadé) | `AppId`, `Name`, `Navigation`, `Http` (`BaseAddress` = adresse du site, cookies de l'onglet), `Title`, `SetTitle`, `TitleChanged`. |
| `HackSiteNavigationManager` | `NavigationManager` de l'onglet : `Uri`, `BaseUri`, `NavigateTo(uri, forceLoad, replace)`, `LocationChanged`, `ToBaseRelativePath` ; `IsWithinSite(uri)`. |
| `HackSiteRouter` | `AppAssembly`, `DefaultLayout`, `NotFound` ; gabarits `@page` de Blazor (`{id:int}`, `{x?}`, `{*rest}`), `[Layout]` et layouts imbriqués, paramètres convertis (`string`, entiers, `bool`, `Guid`, `decimal`, `double`, `float`, `DateTime`, `DateOnly`, `TimeOnly`, nullables). |
| `HackSiteLayoutBase` | Base d'un layout (`Body`). |
| `HackNavLink` | `Href`, `Match` (`Prefix`/`All`), `ActiveClass`, attributs libres. |
| `HackPageTitle` | `Title` → titre de l'onglet. |
| `HackWebSiteView` | Vue du navigateur : `Process`, `Url`, `Cookies`, `OnLocationChanged`, `OnNavigate`, `OnTitleChanged`, `OnFaulted`. Les apps n'ont pas à l'utiliser. |
| `IHackWebSiteHost`, `IHackWebSiteProcess` | Contrat implémenté par la plateforme, utilisé par le navigateur. |

Les composants du site dérivent de `HackComponentBase` : leurs événements et
leur cycle de vie s'exécutent sous l'identité du processus de rendu, donc
`Site.Http`, `HackDns`, etc. passent par ses capacités.

### Ce qu'un site ne fait pas

- Injecter `NavigationManager`, `Router`, `NavLink`, `PageTitle` de Blazor : ce
  sont ceux de l'OS. Le SDK fournit leurs équivalents ci-dessus.
- Déclarer `filesystem.*` : la racine Blazor n'a pas plus de droits que le site.
- Poster un `<form>` sans gestionnaire : aucune navigation de document dans
  l'OS ; un site poste par `EditForm`/`@onsubmit` et `Site.Http`.

## Décisions clés

- **Pas d'iframe** : du code C# installé, limité par ses capacités, dessiné par
  le moteur de rendu de l'OS (ADR 0073 §1) ; le JavaScript des pages HTML reste
  isolé (ADR 0075).
- **Processus de rendu auxiliaire** plutôt que l'instance du site : identité
  claire dans `ps` et le gestionnaire de tâches, arrêt indépendant, activation à
  la demande intacte.
- **Le site répond comme un site Blazor WebAssembly** (page hôte, chargeur,
  `boot.json`) : `curl` voit la même chose qu'avec un vrai site Blazor, et le
  navigateur n'a pas de liste d'apps à consulter avant de demander la page.
- **Interception des liens en phase de capture** : seule façon de passer avant
  le routeur de l'OS sans toucher à l'hôte.
- **Zone de page rendue à une seule place** (navigateur) : `DisplaySurfaceContent`
  accepte un `Surface` nul, si bien qu'un site Blazor garde ses composants et
  leur état au détachement et au rattachement (ADR 0084 §7). La page HTML reste
  réécrite au déplacement (ADR 0081 §4).
- **Site d'exemple installé** : la seule façon de le voir dans les deux hôtes
  (le catalogue vient de `BuiltInApplications.json` de l'Ecosystem) ; caché du
  lanceur comme toute app `Web`.

## Limites connues

- Les requêtes du site par `Site.Http` n'apparaissent pas dans l'onglet Network
  (seuls le document et `blazor.boot.json` y sont).
- Pas de `[SupplyParameterFromQuery]` : les paramètres viennent du chemin.
- Un seul site Blazor par onglet ; un onglet par fenêtre (comme avant).
- Un site Blazor est reconnu par sa page hôte : un site qui écrit sa propre page
  HTML avec `_framework/blazor.hack.js` est traité comme un site Blazor.
- Les processus de rendu s'exécutent sur le fil principal (ADR 0073 §1).

## Tests

| Suite | Ce qui est vérifié |
|---|---|
| `Tests/HackerOs.App.Abstractions.Tests` | `BlazorRoot` → `web.blazor` (mapper, sérialiseur, schéma `web-blazor.valid.json`, validateur). |
| `Tests/HackerOs.AppSdk.Web.Tests/Hosting/BlazorSiteEndpointsTests.cs` | Page hôte pour les chemins sans extension, `404` avec extension, routes du site d'abord, `boot.json`, `blazor.hack.js`, site sans racine. |
| `Tests/HackerOs.AppSdk.Web.Blazor.Tests` | Routeur (gabarits, préséance, conversion), navigation (`NavigateTo`, `SetLocation`, `IsWithinSite`), `HackWebSite` (titre, client). |
| `Tests/HackerOs.Platform.Core.Tests` | `TryGetSingleton` ignore les auxiliaires ; `StartAuxiliaryAsync` (parent, `argv`, capacités, pas l'instance du site, arrêt en `Fault`). |
| `Tests/HackerOs.Platform.Blazor.Tests/HackWebSiteHostTests.cs` | App trouvée par origine, `boot.json` d'une autre app refusé, racine absente ou non-composant, arrêt et `Exited`. |
| `Tests/HackerOs.Network.Tests` | `HackHttpClient.BaseAddress`. |
| `Tests/HackerOs.Platform.WorkerHost.Tests/BlazorSiteTests.cs` | `curl` sur un site à racine Blazor : page hôte, `boot.json`, chargeur, API, `404`, fil principal et worker. |
| `Tests/HackerOs.UI.E2E.Tests/BrowserBlazorSiteTests.cs` | Chromium : site d'exemple dessiné sans iframe, compteur, lien sans rechargement, API, précédent/suivant dans le site, « Aw, Snap! », rechargement, sortie du site. |

## Tâches

- [x] ADR 0084.
- [x] `BlazorRoot`, manifeste `web.blazor` (schéma, sérialiseur, validateur, mapper).
- [x] `ProcessStartRequest.Auxiliary`, `TryGetSingleton`, `StartAuxiliaryAsync`.
- [x] Page hôte, chargeur et `boot.json` (`HackBlazorSiteEndpoints`, `MapFallback`).
- [x] `HackHttpClient.BaseAddress`.
- [x] Bibliothèque `HackerOs.AppSdk.Web.Blazor` (vue, site, navigation, routeur, layout, lien, titre, frontière d'erreur).
- [x] `HackWebSiteHost` (Platform.Blazor), enregistrement dans l'Ecosystem.
- [x] Navigateur : démarrage, navigation interne, précédent/suivant, « Aw, Snap! », arrêt, fenêtre détachée sans perte d'état.
- [x] Site d'exemple dans les deux hôtes.
- [x] Tests unitaires, `curl` et E2E.
- [x] Documentation et index.
