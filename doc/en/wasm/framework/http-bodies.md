# Real HTTP bodies in the simulated network

**Web phase, work item `web/2-http-bodies`** ([`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md),
section 5). Decision: [ADR 0074](../dev/adr/0074-listening-ports-and-name-resolution.md)
§1. Complements [`sdk-network.md`](sdk-network.md) (ADR 0056 §6).

## Purpose

Before this work item, a simulated response only carried a structured
`SimulatedPage` and a request only a `POST` form. There was no way to
send or receive HTML, CSS, JavaScript, JSON or bytes.

Now, as on the wire of a real HTTP server:

- every request and every response can carry a **body**: bytes and the
  `Content-Type` that says how to read them;
- all methods go through: `GET`, `HEAD`, `POST`, `PUT`, `PATCH`,
  `DELETE`, `OPTIONS`;
- redirects follow browser rules (303, 307, 308);
- `curl` writes the raw body; the browser displays a body that is not a
  structured page according to its type.

`SimulatedPage` remains a valid response: the historical sites (HackMail,
HackerSearch, bank, forum, darknet) do not change model. It is the base
on which `web/3-listener-dns` (sites that listen on a port) and
`web/4-browser-html` (isolated HTML rendering) build.

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

### Simulated contracts (`HackerOs.Simulation.Abstractions.Network`)

| Type | Role |
|---|---|
| `SimulatedHttpBody` | Immutable body: full `ContentType`, `MediaType`, `Charset`, `Content` (`ReadOnlyMemory<byte>`), `Length`, `ReadAsString()` (according to the charset, UTF-8 by default), `ReadAsForm()`. Factories `FromText`, `Html`, `Json`, `FromBytes`, `FormUrlEncoded`. |
| `SimulatedMediaTypes` | Constants (`text/html`, `application/json`, `application/x-www-form-urlencoded`...) and `Content-Type` analysis: `GetMediaType`, `GetCharset`, `GetEncoding`, `IsText`, `IsJson`. |
| `SimulatedHttpRequest` | Gains `Body` (replaces `TextBody`, never used). `Create(method, host, path, query, headers, cookies, body)` fills `FormBody` when the body is a form, whatever the method, and sets `Content-Type`/`Content-Length`. Case-insensitive headers. |
| `SimulatedHttpResponse` | Gains `Body`, which takes precedence over `Page` on the wire. Factories `Content`, `Text`, `Html`, `Json`, `File`, `NoContent`, `MethodNotAllowed`. `IsRedirect` recognizes 301, 302, 303, 307 and 308. |
| `SimulatedHttpMethods` | Gains `Patch`, `Options` and `IsSafe` (`GET`, `HEAD`, `OPTIONS`). |
| `SimulatedHttpStatus` | Gains `NoContent` (204), `MovedPermanently` (301), `TemporaryRedirect` (307), `PermanentRedirectKeepMethod` (308), `MethodNotAllowed` (405). |
| `ISimulatedNetworkService.Send` | One exchange, without following redirects, with cookies: returns `SimulatedExchangeResult(Response, NetworkError)`. `Post` now goes through it; `Navigate` remains for compatibility and also returns `Body`. |

### Site router (`SimulatedWebsiteControllerBase`)

Like ASP.NET Core:

- `Map(method, pattern, handler)` and the shortcuts `Get`, `Post`, `Put`,
  `Patch`, `Delete`;
- `HEAD` is served by the `GET` route (the gateway removes the body and keeps
  `Content-Length`);
- a known path requested with a method it does not have answers **405** with
  the `Allow` header; `OPTIONS` on a known path answers **204** with `Allow`;
- an unknown path always answers 404.

### Gateway (`AppNetworkGateway`)

- Capability: `network.simulated.read` for a safe method (`GET`, `HEAD`,
  `OPTIONS`), `network.simulated.write` for the others.
- Redirect loop (10 at most, then `ERR_TOO_MANY_REDIRECTS`), rules of
  `RedirectBecomesGet`:

  | Status | Next method |
  |---|---|
  | 303 | `GET` without a body, except `HEAD` which stays `HEAD` |
  | 301, 302 | `GET` without a body for a `POST`; same method otherwise |
  | 307, 308 | same method, same body |

- Response returned to the app: the site's headers, then `Content-Type` (that of the
  body; `text/plain; charset=utf-8` for the text rendering of a historical
  page; none when there is neither body nor page, like a real 204 or
  302), `Content-Length`, `Set-Cookie`.
- The relay to the real network (optional server) carries all
  methods and the request body since `v3/10-server-proxy`
  (`ProxyHttpRequest.Body`, size and hash verified by the server,
  [`server-network-relay.md`](../dev/server-network-relay.md)).
- `/proc/net/dev` accounting (ADR 0057): the request size includes
  `Content-Type`, `Content-Length` and the body.

### Web Workers

`WireCodec.WriteHttpRequest` writes the body's type (`ct`) and bytes in base64
(`bd`); the response already carried its bytes (`b`) and its headers. A
command launched in a worker (ADR 0070) therefore sends and receives the same
bytes as on the main thread. `Page` still does not cross: only the
browser, a windowed app, displays it.

## Usage (SDK)

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

| Type | .NET equivalent | Default type |
|---|---|---|
| `HackHttpContent` | `HttpContent` | `application/octet-stream` |
| `HackStringContent(text, encoding?, mediaType?)` | `StringContent` | `text/plain; charset=utf-8` |
| `HackByteArrayContent(bytes, contentType?)` | `ByteArrayContent` | `application/octet-stream` |
| `HackFormUrlEncodedContent(fields)` | `FormUrlEncodedContent` | `application/x-www-form-urlencoded` |
| `HackJsonContent.Create(value, JsonTypeInfo)` / `FromJson(json)` | `JsonContent.Create` | `application/json; charset=utf-8` |

`HackJsonContent.Create<T>(value, options)` and `HackHttpResponse.ReadFromJson<T>(options)`
also exist through reflection, marked `[RequiresUnreferencedCode]`: prefer
the `JsonTypeInfo<T>` overloads in an app published to WebAssembly.

Contract change: `HackHttpRequest.Form` is replaced by `Content`;
`HackHttpResponse.Content` (bytes) becomes `ContentBytes`, and `Content` gives
the text (ADR 0074 §1). `SendAsync(method, uri, content)` takes an
`HackHttpContent` instead of a list of fields;
`PostAsync(uri, fields)` remains.

## Simulated site side

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

Historical sites adapted in this work item: HackerSearch serves
`/api/search?q=…` (JSON written with `Utf8JsonWriter`), `/robots.txt`
(`text/plain`), and its search form, which used to post to a
nonexistent route, receives a `POST /search` that redirects to `GET /search?q=…`.

## `curl`

| Option | Effect |
|---|---|
| `-d`, `--data` | Body sent as is, implicit `POST`, `Content-Type: application/x-www-form-urlencoded`; `@file` reads a file without its line endings; several `-d` are joined by `&`. |
| `--data-raw` | Like `-d`, without interpreting `@`. |
| `--data-binary` | `@file` read without modification. |
| `--data-urlencode name=value` | Encoded value (space as `+`). |
| `--json DATA` | JSON body, `Content-Type` and `Accept` set to `application/json`. |
| `-H "Content-Type: …"` | Replaces the type implied by the data option. |
| `-X METHOD` | `PUT`, `PATCH`, `DELETE`, `OPTIONS`... |
| `-i` | Status line and headers, empty line, then the body. |
| `-v` | Headers sent (`>`) and received (`<`). |
| `-f` | Status ≥ 400: no body, code 22. **Without `-f`, the body is displayed and the code is 0**, like the real `curl` (before this work item, `-f` was always active). |

The body is written raw, decoded according to its charset. A binary body (image,
archive) is not written to the terminal: `curl` displays the warning
"Binary output can mess up your terminal" and exits with 23, except with
`-o FILE` or `-o -`.

## Browser

`BrowserWindow.ShowResponse` chooses the rendering:

1. a structured page (`Page`): `SimulatedPageRenderer`, as before;
2. otherwise a body, presented by `ResponseBodyPresenter` (pure, tested) and drawn
   by `ResponseBodyViewer`:

   | Type | Rendering |
   |---|---|
   | `application/json`, `+json` | Indented JSON (text unchanged if invalid) |
   | `text/html` | Rendered in the isolated iframe since `web/4-browser-html` (ADR 0075, [`browser-html-rendering.md`](../dev/browser-html-rendering.md)); the source remains visible with `view-source:` or the "source" button |
   | `text/*`, XML, JavaScript | Plain text |
   | `image/*` | Image (`data:` URL) on a checkerboard |
   | other | "This file can't be displayed", with the `curl -O` command to use |
   | no type | Sniffed: valid UTF-8 text without a null byte, otherwise binary |

3. an error without a body: "HTTP ERROR 500" error page.

## Decisions

- **`SimulatedHttpBody` is a class, not a `record`**: the equality of a
  record over a byte array would be reference equality, which is misleading.
- **`HackHttpRequest.Form` disappears** instead of coexisting with `Content`:
  a single source of truth for the body; `PostAsync(uri, fields)` remains
  as a shortcut.
- **Redirects are decided by the gateway**, no longer by
  `Navigate`: it is the one that knows the method and the body to replay for
  307/308.
- **`curl` no longer forces `-f`**: ADR 0074 asks for the raw body; the
  default behavior of the real `curl` is to display the body of an error.
- **HTML shown as source** in this work item: real rendering requires the isolated
  iframe and the sub-resource loader of ADR 0075 (`web/4`).
- **No request body to the real network** in this work item: the
  proxy contract did not carry it. Done since by
  `v3/10-server-proxy` (ADR 0102, [`server-network-relay.md`](../dev/server-network-relay.md)).
- ADR 0021 ("no raw HTML") is marked partly replaced by
  ADR 0074 §1 and 0075.

## For `web/3` and `web/4`

- A listener (`HackHttpListener`, `web/3`) receives a `SimulatedHttpRequest`
  with `Body` and answers a `SimulatedHttpResponse` with `Body`: the wire format of
  workers already exists for the client (`ct`, `bd`, `b`).
- The browser (`web/4`) replaces the `Html` branch of `ResponseBodyViewer`
  with the isolated iframe; `HackHttpClient` already knows how to load CSS, JS and images
  with their type, and relay `fetch` with any method and body.

## Task list

- [x] `SimulatedHttpBody`, `SimulatedMediaTypes`, body in `SimulatedHttpRequest` and `SimulatedHttpResponse`.
- [x] `ISimulatedNetworkService.Send` and `SimulatedExchangeResult`; `Post` and `Navigate` adapted.
- [x] Site router: `Map`, `Put`, `Patch`, `Delete`, `HEAD` on `GET`, 405 with `Allow`, `OPTIONS`.
- [x] `HackHttpContent` and variants; `HackHttpRequest.Content`; `HackHttpResponse.ContentType`, `Content`, `ContentBytes`, `GetHeader`, `ReadFromJson`.
- [x] `HackHttpClient`: `PutAsync`, `PatchAsync`, `DeleteAsync`, `GetByteArrayAsync`, `GetFromJsonAsync`, `PostAsJsonAsync`, `SendAsync` with content.
- [x] Gateway: capability per safe method, redirect loop (303/307/308), site headers, real `Content-Type`.
- [x] Worker protocol (`WireCodec`) and network accounting.
- [x] `curl`: raw body, `-i`, `--json`, `--data-*`, `@file`, optional `-f`, binary refusal; `wget` on `ContentBytes`.
- [x] Browser: `ResponseBodyPresenter` and `ResponseBodyViewer`.
- [x] HackerSearch: `/api/search`, `/robots.txt`, `POST /search`.
- [x] Tests: network and SDK (`Wave4NetworkTests.HttpBodies`), `curl`, worker protocol (`WireCodecHttpTests`), browser (`ResponseBodyPresenterTests`).
- [x] Documentation: this document, `sdk-network.md`, `sdk-reference.md`, `linux-commands.md`, plan, status of ADR 0021, index.
