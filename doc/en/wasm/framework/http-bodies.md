# Corps HTTP réels dans le réseau simulé

**Phase web, chantier `web/2-http-bodies`** ([`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md),
section 5). Décision : [ADR 0074](../dev/adr/0074-listening-ports-and-name-resolution.md)
§1. Complète [`sdk-network.md`](sdk-network.md) (ADR 0056 §6).

## Objet

Avant ce chantier, une réponse simulée ne portait qu'un `SimulatedPage`
structuré et une requête qu'un formulaire `POST`. Il n'y avait aucun moyen
d'envoyer ou de recevoir du HTML, du CSS, du JavaScript, du JSON ou des octets.

Désormais, comme sur le fil d'un vrai serveur HTTP :

- toute requête et toute réponse peut porter un **corps** : des octets et le
  `Content-Type` qui dit comment les lire ;
- toutes les méthodes passent : `GET`, `HEAD`, `POST`, `PUT`, `PATCH`,
  `DELETE`, `OPTIONS` ;
- les redirections suivent les règles des navigateurs (303, 307, 308) ;
- `curl` écrit le corps brut ; le navigateur affiche un corps qui n'est pas une
  page structurée selon son type.

`SimulatedPage` reste une réponse valide : les sites historiques (HackMail,
HackerSearch, banque, forum, darknet) ne changent pas de modèle. C'est la base
sur laquelle `web/3-listener-dns` (sites qui écoutent un port) et
`web/4-browser-html` (rendu HTML isolé) s'appuient.

## Architecture

```
App (navigateur, curl, wget, app tierce)
  └─ HackHttpClient ── HackHttpRequest(Method, Uri, Headers, Content, Cookies)   (HackerOs.AppSdk.Net)
       └─ IAppNetworkGateway.SendAsync   (Web Worker : WireCodec "ct" + "bd", ADR 0070)
            └─ AppNetworkGateway          capacité, boucle de redirections, comptage /proc/net/dev
                 └─ ISimulatedNetworkService.Send(method, url, headers, body, jar)
                      └─ ISimulatedWebsiteController.ProcessRequest(SimulatedHttpRequest)
                           → SimulatedHttpResponse(Status, Page?, RedirectUrl?, SetCookies, Headers, Body?)
```

### Contrats simulés (`HackerOs.Simulation.Abstractions.Network`)

| Type | Rôle |
|---|---|
| `SimulatedHttpBody` | Corps immuable : `ContentType` complet, `MediaType`, `Charset`, `Content` (`ReadOnlyMemory<byte>`), `Length`, `ReadAsString()` (selon le charset, UTF-8 par défaut), `ReadAsForm()`. Fabriques `FromText`, `Html`, `Json`, `FromBytes`, `FormUrlEncoded`. |
| `SimulatedMediaTypes` | Constantes (`text/html`, `application/json`, `application/x-www-form-urlencoded`…) et analyse d'un `Content-Type` : `GetMediaType`, `GetCharset`, `GetEncoding`, `IsText`, `IsJson`. |
| `SimulatedHttpRequest` | Gagne `Body` (remplace `TextBody`, jamais utilisé). `Create(method, host, path, query, headers, cookies, body)` remplit `FormBody` quand le corps est un formulaire, quelle que soit la méthode, et pose `Content-Type`/`Content-Length`. En-têtes insensibles à la casse. |
| `SimulatedHttpResponse` | Gagne `Body`, qui l'emporte sur `Page` sur le fil. Fabriques `Content`, `Text`, `Html`, `Json`, `File`, `NoContent`, `MethodNotAllowed`. `IsRedirect` reconnaît 301, 302, 303, 307 et 308. |
| `SimulatedHttpMethods` | Gagne `Patch`, `Options` et `IsSafe` (`GET`, `HEAD`, `OPTIONS`). |
| `SimulatedHttpStatus` | Gagne `NoContent` (204), `MovedPermanently` (301), `TemporaryRedirect` (307), `PermanentRedirectKeepMethod` (308), `MethodNotAllowed` (405). |
| `ISimulatedNetworkService.Send` | Un échange, sans suivre les redirections, avec cookies : rend `SimulatedExchangeResult(Response, NetworkError)`. `Post` passe désormais par lui ; `Navigate` reste pour la compatibilité et rend aussi `Body`. |

### Routeur des sites (`SimulatedWebsiteControllerBase`)

Comme ASP.NET Core :

- `Map(method, motif, handler)` et les raccourcis `Get`, `Post`, `Put`,
  `Patch`, `Delete` ;
- `HEAD` est servi par la route `GET` (la passerelle retire le corps et garde
  `Content-Length`) ;
- un chemin connu demandé avec une méthode qu'il n'a pas répond **405** avec
  l'en-tête `Allow` ; `OPTIONS` sur un chemin connu répond **204** avec `Allow` ;
- un chemin inconnu répond toujours 404.

### Passerelle (`AppNetworkGateway`)

- Capacité : `network.simulated.read` pour une méthode sûre (`GET`, `HEAD`,
  `OPTIONS`), `network.simulated.write` pour les autres.
- Boucle de redirections (10 au plus, puis `ERR_TOO_MANY_REDIRECTS`), règles de
  `RedirectBecomesGet` :

  | Statut | Méthode suivante |
  |---|---|
  | 303 | `GET` sans corps, sauf `HEAD` qui reste `HEAD` |
  | 301, 302 | `GET` sans corps pour un `POST` ; même méthode sinon |
  | 307, 308 | même méthode, même corps |

- Réponse rendue à l'app : en-têtes du site, puis `Content-Type` (celui du
  corps ; `text/plain; charset=utf-8` pour le rendu texte d'une page
  historique ; aucun quand il n'y a ni corps ni page, comme un vrai 204 ou
  302), `Content-Length`, `Set-Cookie`.
- Le relais vers le réseau réel (serveur optionnel) transporte toutes les
  méthodes et le corps de la requête depuis `v3/10-server-proxy`
  (`ProxyHttpRequest.Body`, taille et hash vérifiés par le serveur,
  [`server-network-relay.md`](../dev/server-network-relay.md)).
- Comptage `/proc/net/dev` (ADR 0057) : la taille de la requête inclut
  `Content-Type`, `Content-Length` et le corps.

### Web Workers

`WireCodec.WriteHttpRequest` écrit le type (`ct`) et les octets en base64
(`bd`) du corps ; la réponse portait déjà ses octets (`b`) et ses en-têtes. Une
commande lancée dans un worker (ADR 0070) envoie et reçoit donc les mêmes
octets que sur le fil principal. `Page` ne traverse toujours pas : seul le
navigateur, une app à fenêtre, l'affiche.

## Utilisation (SDK)

```csharp
using HackHttpClient http = new();

// Corps typés, calqués sur System.Net.Http.
HackHttpResponse created = await http.PostAsync("https://api.hack/items", new HackStringContent("hello"));
HackHttpResponse updated = await http.PutAsync("https://api.hack/items/7", HackJsonContent.FromJson("{\"done\":true}"));
HackHttpResponse patched = await http.PatchAsync("https://api.hack/items/7",
    new HackFormUrlEncodedContent([new("title", "nouveau titre")]));
HackHttpResponse deleted = await http.DeleteAsync("https://api.hack/items/7");      // 204, sans Content-Type

// JSON sans réflexion (trim-safe) avec un contexte source-généré.
Item? item = await http.GetFromJsonAsync("https://api.hack/items/7", MyJsonContext.Default.Item);
HackHttpResponse posted = await http.PostAsJsonAsync("https://api.hack/items", item!, MyJsonContext.Default.Item);

// Lecture de la réponse.
string type = updated.ContentType!;          // "application/json; charset=utf-8"
string media = updated.MediaType;            // "application/json"
string text = updated.Content;               // décodé avec le charset (UTF-8 par défaut)
byte[] raw = updated.ContentBytes;           // octets bruts
string? allow = updated.GetHeader("Allow");  // premier en-tête de ce nom, sans casse
```

| Type | Équivalent .NET | Type par défaut |
|---|---|---|
| `HackHttpContent` | `HttpContent` | `application/octet-stream` |
| `HackStringContent(text, encoding?, mediaType?)` | `StringContent` | `text/plain; charset=utf-8` |
| `HackByteArrayContent(bytes, contentType?)` | `ByteArrayContent` | `application/octet-stream` |
| `HackFormUrlEncodedContent(fields)` | `FormUrlEncodedContent` | `application/x-www-form-urlencoded` |
| `HackJsonContent.Create(value, JsonTypeInfo)` / `FromJson(json)` | `JsonContent.Create` | `application/json; charset=utf-8` |

`HackJsonContent.Create<T>(value, options)` et `HackHttpResponse.ReadFromJson<T>(options)`
existent aussi par réflexion, marqués `[RequiresUnreferencedCode]` : préférer
les surcharges `JsonTypeInfo<T>` dans une app publiée en WebAssembly.

Changement de contrat : `HackHttpRequest.Form` est remplacé par `Content` ;
`HackHttpResponse.Content` (octets) devient `ContentBytes`, et `Content` donne
le texte (ADR 0074 §1). `SendAsync(method, uri, content)` prend un
`HackHttpContent` au lieu d'une liste de champs ;
`PostAsync(uri, champs)` reste.

## Côté site simulé

```csharp
public sealed class ApiController : SimulatedWebsiteControllerBase
{
    public ApiController()
    {
        Get("/api/items/{id}", req => SimulatedHttpResponse.Json($"{{\"id\":\"{req.RouteParams["id"]}\"}}"));
        Put("/api/items/{id}", req => req.ContentType?.StartsWith("application/json") == true
            ? SimulatedHttpResponse.Json(req.ReadBodyAsString())
            : SimulatedHttpResponse.Text("JSON attendu", 415));
        Delete("/api/items/{id}", _ => SimulatedHttpResponse.NoContent());
        Get("/logo.png", _ => SimulatedHttpResponse.File(LogoBytes, "image/png"));
    }
    // PrimaryHostname, Theme, SiteName…
}
```

Sites historiques adaptés dans ce chantier : HackerSearch sert
`/api/search?q=…` (JSON écrit avec `Utf8JsonWriter`), `/robots.txt`
(`text/plain`), et son formulaire de recherche, qui postait vers une route
inexistante, reçoit un `POST /search` qui redirige vers `GET /search?q=…`.

## `curl`

| Option | Effet |
|---|---|
| `-d`, `--data` | Corps envoyé tel quel, `POST` implicite, `Content-Type: application/x-www-form-urlencoded` ; `@fichier` lit un fichier sans ses fins de ligne ; plusieurs `-d` sont joints par `&`. |
| `--data-raw` | Comme `-d`, sans interprétation de `@`. |
| `--data-binary` | `@fichier` lu sans modification. |
| `--data-urlencode nom=valeur` | Valeur encodée (espace en `+`). |
| `--json DONNÉES` | Corps JSON, `Content-Type` et `Accept` à `application/json`. |
| `-H "Content-Type: …"` | Remplace le type impliqué par l'option de données. |
| `-X MÉTHODE` | `PUT`, `PATCH`, `DELETE`, `OPTIONS`… |
| `-i` | Ligne de statut et en-têtes, ligne vide, puis le corps. |
| `-v` | En-têtes envoyés (`>`) et reçus (`<`). |
| `-f` | Statut ≥ 400 : pas de corps, code 22. **Sans `-f`, le corps est affiché et le code est 0**, comme le vrai `curl` (avant ce chantier, `-f` était toujours actif). |

Le corps est écrit brut, décodé selon son charset. Un corps binaire (image,
archive) n'est pas écrit dans le terminal : `curl` affiche l'avertissement
« Binary output can mess up your terminal » et sort avec 23, sauf avec
`-o FICHIER` ou `-o -`.

## Navigateur

`BrowserWindow.ShowResponse` choisit le rendu :

1. une page structurée (`Page`) : `SimulatedPageRenderer`, comme avant ;
2. sinon un corps, présenté par `ResponseBodyPresenter` (pur, testé) et dessiné
   par `ResponseBodyViewer` :

   | Type | Rendu |
   |---|---|
   | `application/json`, `+json` | JSON indenté (texte inchangé s'il est invalide) |
   | `text/html` | Rendu dans l'iframe isolée depuis `web/4-browser-html` (ADR 0075, [`browser-html-rendering.md`](../dev/browser-html-rendering.md)) ; la source reste visible avec `view-source:` ou le bouton « source » |
   | `text/*`, XML, JavaScript | Texte brut |
   | `image/*` | Image (`data:` URL) sur damier |
   | autre | « This file can't be displayed », avec la commande `curl -O` à utiliser |
   | sans type | Reniflé : texte UTF-8 valide sans octet nul, sinon binaire |

3. une erreur sans corps : page d'erreur « HTTP ERROR 500 ».

## Décisions

- **`SimulatedHttpBody` est une classe, pas un `record`** : l'égalité d'un
  record sur un tableau d'octets serait une égalité de références, trompeuse.
- **`HackHttpRequest.Form` disparaît** au lieu de cohabiter avec `Content` :
  une seule source de vérité pour le corps ; `PostAsync(uri, champs)` reste
  comme raccourci.
- **Les redirections sont décidées par la passerelle**, plus par
  `Navigate` : c'est elle qui connaît la méthode et le corps à rejouer pour
  307/308.
- **`curl` ne force plus `-f`** : l'ADR 0074 demande le corps brut ; le
  comportement par défaut du vrai `curl` est d'afficher le corps d'une erreur.
- **HTML montré en source** dans ce chantier : le rendu réel exige l'iframe
  isolée et le chargeur de sous-ressources de l'ADR 0075 (`web/4`).
- **Pas de corps de requête vers le réseau réel** dans ce chantier : le
  contrat du proxy ne le transportait pas. Fait depuis par
  `v3/10-server-proxy` (ADR 0102, [`server-network-relay.md`](../dev/server-network-relay.md)).
- L'ADR 0021 (« pas de HTML brut ») est marqué remplacé en partie par les
  ADR 0074 §1 et 0075.

## Pour `web/3` et `web/4`

- Un listener (`HackHttpListener`, `web/3`) reçoit un `SimulatedHttpRequest`
  avec `Body` et répond un `SimulatedHttpResponse` avec `Body` : le format sur
  le fil des workers existe déjà pour le client (`ct`, `bd`, `b`).
- Le navigateur (`web/4`) remplace la branche `Html` de `ResponseBodyViewer`
  par l'iframe isolée ; `HackHttpClient` sait déjà charger CSS, JS et images
  avec leur type, et relayer `fetch` avec n'importe quelle méthode et corps.

## Liste des tâches

- [x] `SimulatedHttpBody`, `SimulatedMediaTypes`, corps dans `SimulatedHttpRequest` et `SimulatedHttpResponse`.
- [x] `ISimulatedNetworkService.Send` et `SimulatedExchangeResult` ; `Post` et `Navigate` adaptés.
- [x] Routeur des sites : `Map`, `Put`, `Patch`, `Delete`, `HEAD` sur `GET`, 405 avec `Allow`, `OPTIONS`.
- [x] `HackHttpContent` et variantes ; `HackHttpRequest.Content` ; `HackHttpResponse.ContentType`, `Content`, `ContentBytes`, `GetHeader`, `ReadFromJson`.
- [x] `HackHttpClient` : `PutAsync`, `PatchAsync`, `DeleteAsync`, `GetByteArrayAsync`, `GetFromJsonAsync`, `PostAsJsonAsync`, `SendAsync` avec contenu.
- [x] Passerelle : capacité par méthode sûre, boucle de redirections (303/307/308), en-têtes du site, `Content-Type` réel.
- [x] Protocole des workers (`WireCodec`) et comptage réseau.
- [x] `curl` : corps brut, `-i`, `--json`, `--data-*`, `@fichier`, `-f` facultatif, refus du binaire ; `wget` sur `ContentBytes`.
- [x] Navigateur : `ResponseBodyPresenter` et `ResponseBodyViewer`.
- [x] HackerSearch : `/api/search`, `/robots.txt`, `POST /search`.
- [x] Tests : réseau et SDK (`Wave4NetworkTests.HttpBodies`), `curl`, protocole des workers (`WireCodecHttpTests`), navigateur (`ResponseBodyPresenterTests`).
- [x] Documentation : ce document, `sdk-network.md`, `sdk-reference.md`, `linux-commands.md`, plan, statut de l'ADR 0021, index.
